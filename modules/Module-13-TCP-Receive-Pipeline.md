# Модуль 13: TCP Receive Pipeline

*«От отдельных компонентов к полному приёмному конвейеру»*

> **Исходный код:** `src/TcpReceivePipeline/`  
> **Запуск:** `dotnet run --project src/TcpReceivePipeline`  
> **Предыдущий модуль:** [Модуль 12: Out-of-Order Reassembly](Module-12-Out-of-Order-Reassembly.md)

---

В модулях 11 и 12 мы построили фундамент: парсер TCP-заголовка, скользящее окно, проверку допустимости сегментов и реассемблер потока с обработкой out-of-order данных. Каждый из этих компонентов работал изолированно — мы скармливали ему входные данные и проверяли результат.

Но настоящий TCP — не набор отдельных деталей. Это **замкнутый контур управления** (closed-loop control system), где получатель влияет на отправителя через подтверждения (ACK), а отправитель адаптирует скорость передачи на основе обратной связи. В этом модуле мы собираем все компоненты в единый приёмный конвейер и добавляем три критически важных механизма, которых не было в предыдущих модулях:

1. **Delayed ACK** — отложенные подтверждения (RFC 1122)
2. **Retransmission Timer и Fast Retransmit** — обнаружение потерь (RFC 6298, RFC 5681)
3. **Congestion Control** — управление перегрузкой (RFC 5681)

Результатом станет система, которая способна передавать данные, обнаруживать потери, ретранслировать утерянные сегменты и адаптировать скорость к состоянию сети — всё в рамках одного детерминированного прогона с виртуальным временем.

---

## Часть 13.1 — Архитектура полного Pipeline

### Полный путь приёма сегмента

Когда Ethernet-кадр прибывает на сетевой интерфейс, он проходит следующий путь до того, как данные станут доступны приложению:

```
Ethernet Frame
    |
    v
IPv4 Header Parse + Reassembly
    |
    v
TCP Header Parse
    |
    v
Checksum Verify
    |
    v
State Machine (SYN_RCVD, ESTABLISHED, ...)
    |
    v
Segment Acceptability Test (RFC 9293 S3.4)
    |
    v
Window Validation
    |
    v
Reassembler (OOO buffering + in-order delivery)
    |
    v
ACK Generator (delayed / immediate)
    |
    v
Application (read() / recv())
```

В модулях 11-12 мы реализовали среднюю часть этого конвейера: от парсинга TCP-заголовка до реассемблера. Ethernet/IPv4 были рассмотрены в модулях 1-2. Машина состояний TCP (SYN/FIN/RST) — тема модуля 3.

В этом модуле нас интересует то, что превращает конвейер в **замкнутый контур**. Прямой путь (сверху вниз) — это data path. Но есть и обратный путь:

```
Application
    |
    v
Reassembler      ─────────────────────┐
    |                                  |
    v                                  v
ACK Generator    ──────>  Outbound ACK (ack=RcvNxt, win=AdvertisedWindow)
                                       |
                                       v
                           Peer's Send Side
                                       |
                                       v
                         Retransmission Controller
                                       |
                                       v
                         Congestion Controller
                                       |
                                       v
                         "Сколько можно отправить?"
                              usable = min(cwnd, rwnd) - bytesInFlight
```

Каждый ACK, отправленный получателем, несёт два сигнала: (1) **acknowledgment number** — «какие байты я получил», и (2) **advertised window** — «сколько ещё я готов принять». Отправитель использует оба сигнала плюс своё внутреннее состояние (congestion window) для решения, сколько данных отправить следующими.

Именно эта обратная связь делает TCP **self-clocking** протоколом: ACK от получателя «тактирует» отправку следующих данных.

---

## Часть 13.2 — Разделение SlidingWindowTracker

### Зачем разделять

В модуле 11 мы реализовали `SlidingWindowTracker` — единый класс, отвечающий за обе стороны окна: отправителя (SND.UNA, SND.NXT, SND.WND) и получателя (RCV.NXT, RCV.WND). Это было удобно для демонстрации концепции, но в полном pipeline разделение необходимо по нескольким причинам:

1. **Разная ответственность.** Отправитель отвечает за tracking bytes-in-flight и usable window. Получатель отвечает за реассемблинг и вычисление честного advertised window.
2. **Разные зависимости.** Send window зависит от congestion controller (cwnd). Receive endpoint зависит от TcpStreamReassembler из модуля 12.
3. **Разный жизненный цикл.** Send window обновляется при отправке сегментов и получении ACK. Receive endpoint обновляется при получении данных и чтении приложением.

### TcpSendWindow.cs

Отвечает исключительно за переменные отправителя. Обратите внимание на unchecked-арифметику — номера последовательностей TCP являются 32-битными и оборачиваются через 2^32:

```csharp
namespace TcpReceivePipeline.Core;

public sealed class TcpSendWindow
{
    public uint SndUna { get; private set; }
    public uint SndNxt { get; private set; }
    public ushort SndWnd { get; private set; }

    public TcpSendWindow(uint initialSendSeq, ushort peerWindow)
    {
        SndUna = initialSendSeq;
        SndNxt = initialSendSeq;
        SndWnd = peerWindow;
    }

    /// <summary>
    /// Количество байт, находящихся «в полёте» — отправленных, но ещё не подтверждённых.
    /// SND.NXT - SND.UNA с защитой от wraparound.
    /// </summary>
    public uint BytesInFlight => unchecked(SndNxt - SndUna);

    /// <summary>
    /// Количество байт, которые можно отправить прямо сейчас.
    /// UsableWindow = SND.WND - BytesInFlight, но не меньше нуля.
    /// Проверка SndWnd - BytesInFlight > SndWnd обнаруживает отрицательный результат
    /// (unsigned underflow), в этом случае возвращаем 0.
    /// </summary>
    public uint UsableWindow => SndWnd - BytesInFlight > SndWnd ? 0 : SndWnd - BytesInFlight;

    /// <summary>
    /// Вызывается при отправке сегмента данных.
    /// Продвигает SND.NXT на length байт.
    /// </summary>
    public void OnSegmentSent(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        SndNxt = unchecked(SndNxt + (uint)length);
    }

    /// <summary>
    /// Вызывается при получении ACK от peer.
    /// Возвращает false, если ackNumber > SND.NXT (невалидный ACK — подтверждает
    /// данные, которые мы ещё не отправляли).
    /// </summary>
    public bool OnAckReceived(uint ackNumber, ushort advertisedWindow)
    {
        // ACK за пределами SND.NXT — peer подтверждает то, что мы не отправляли
        if (TcpSequence.GreaterThan(ackNumber, SndNxt))
            return false;

        // Продвигаем SND.UNA, если ACK подтверждает новые данные
        if (TcpSequence.GreaterThan(ackNumber, SndUna))
            SndUna = ackNumber;

        // Всегда обновляем window — peer мог изменить его даже без нового ACK
        SndWnd = advertisedWindow;
        return true;
    }
}
```

Свойство `UsableWindow` — центральное для всего pipeline. Именно его значение определяет, может ли отправитель выпустить новые данные. Однако обратите внимание: здесь учитывается только **receiver window** (SND.WND — то, что peer рекламирует). Congestion window (cwnd) будет добавлен позже в CongestionController, и формула станет:

```
usable = min(cwnd, SndWnd) - BytesInFlight
```

### TcpReceiveEndpoint.cs

Обёртка над `TcpStreamReassembler` из модуля 12, добавляющая вычисление `AdvertisedWindow`:

```csharp
namespace TcpReceivePipeline.Core;

public sealed class TcpReceiveEndpoint : IDisposable
{
    private readonly TcpStreamReassembler _reassembler;
    private readonly int _capacity;

    public TcpReceiveEndpoint(
        uint initialReceiveSequence,
        int capacity,
        int maxBufferedRanges = 4096,
        ArrayPool<byte>? pool = null)
    {
        _capacity = capacity;
        _reassembler = new TcpStreamReassembler(
            initialReceiveSequence,
            receiveWindow: capacity,
            maxBufferedRanges: maxBufferedRanges,
            pool: pool);
    }

    /// <summary>
    /// Следующий ожидаемый sequence number — правая граница непрерывного
    /// буфера от начала потока. Это значение уходит в ACK.ack.
    /// </summary>
    public uint RcvNxt => _reassembler.RcvNxt;

    /// <summary>
    /// Суммарный объём буферизированных данных, включая out-of-order.
    /// </summary>
    public long BufferedBytes => _reassembler.BufferedBytes;

    /// <summary>
    /// Количество отдельных диапазонов в буфере.
    /// 1 = весь буфер непрерывен. >1 = есть пропуски (gap).
    /// </summary>
    public int BufferedRangeCount => _reassembler.BufferedRangeCount;

    /// <summary>
    /// Честное окно, которое мы рекламируем peer-у.
    /// capacity - BufferedBytes: мы вычитаем ВСЕ буферизированные данные,
    /// включая out-of-order, потому что они реально занимают память.
    /// 
    /// Это отличается от наивной реализации, которая вычитает только
    /// in-order данные. Наш подход честнее: если peer заполнит все дыры,
    /// данные мгновенно станут in-order, и буфер будет полон.
    /// </summary>
    public ushort AdvertisedWindow
    {
        get
        {
            long free = _capacity - _reassembler.BufferedBytes;
            if (free < 0) free = 0;
            return (ushort)Math.Min(free, ushort.MaxValue);
        }
    }

    /// <summary>
    /// Передаёт входящий сегмент в реассемблер.
    /// onDataReady вызывается синхронно для каждого непрерывного
    /// блока данных, готового к передаче приложению.
    /// </summary>
    public SegmentInsertResult Receive(
        TcpSegment segment,
        TcpDataReadyHandler onDataReady)
        => _reassembler.Push(segment.SequenceNumber, segment.Payload, onDataReady);

    public void Dispose() => _reassembler.Dispose();
}
```

Ключевой момент: `AdvertisedWindow = capacity - BufferedBytes`. Мы вычитаем **все** буферизированные данные, включая out-of-order фрагменты. Почему? Потому что эти фрагменты занимают реальную память. Если мы не учтём их, peer может отправить столько данных, что буфер переполнится. Это тонкий, но критически важный аспект: AdvertisedWindow — это не «сколько последовательных байт я могу принять», а «сколько памяти у меня осталось».

---

## Часть 13.3 — Появляется время: виртуальные тики

### Почему модули 11-12 не нуждались во времени

В модулях 11 и 12 нас интересовал только **порядок событий**: сегмент A пришёл до сегмента B, ACK отправлен после получения данных. Все операции были мгновенными и детерминированными. Мы могли написать:

```csharp
reassembler.Push(1001, "ABCD"u8);
reassembler.Push(1009, "IJKL"u8);  // out-of-order
reassembler.Push(1005, "EFGH"u8);  // fills the gap
```

И не задумываться о том, **когда** каждый из этих вызовов произошёл. Реассемблеру всё равно — он работает с номерами последовательностей, а не с часами.

### Зачем нужно время теперь

Два механизма в этом модуле принципиально зависят от времени:

1. **Delayed ACK.** RFC 1122 требует: «ACK должен быть отправлен не позднее чем через 500 мс после получения данных». Для этого нужен **таймер**: если за 500 мс (или, в нашей реализации, за настраиваемый maxDelay) не произошло события, которое вызвало бы немедленный ACK, таймер срабатывает и отправляет ACK принудительно.

2. **Retransmission Timeout (RTO).** RFC 6298 требует: «если ACK на отправленный сегмент не получен в течение RTO, сегмент считается потерянным и ретранслируется». RTO вычисляется на основе измеренного RTT — и снова нужны часы.

### PriorityQueue как очередь событий

Мы используем `PriorityQueue<Action, double>` из .NET 6+ как минимальную кучу, упорядоченную по виртуальному времени:

```csharp
var eventQueue = new PriorityQueue<Action, double>();

// Запланировать событие на время t=5.0
eventQueue.Enqueue(() => Console.WriteLine("Timer fired!"), 5.0);

// Основной цикл
while (eventQueue.TryDequeue(out var action, out var scheduledTime))
{
    currentTime = scheduledTime;
    action();
}
```

Виртуальное время — это просто `double`, не привязанный к реальным часам. Мы полностью контролируем его: события происходят мгновенно, один за другим, в порядке возрастания scheduledTime. Это даёт два критических преимущества:

1. **Детерминизм.** Тест всегда выдаёт одинаковый результат. Нет race conditions, нет зависимости от загрузки CPU.
2. **Скорость.** Прогон, моделирующий 10 секунд сетевого взаимодействия, завершается за микросекунды.

В production TCP-стеке (например, Linux tcp_input.c) используются реальные таймеры ядра (jiffies, hrtimers). Наш подход — учебный аналог, сохраняющий всю логику без операционной системы.

---

## Часть 13.4 — Generation-счётчик: отмена таймеров без удаления из очереди

### Проблема

`PriorityQueue<T, TPriority>` в .NET не поддерживает операцию `Cancel()` или `Remove()`. Если мы запланировали callback на время t=5.0, а в момент t=3.0 хотим его отменить (например, ACK пришёл вовремя и RTO-таймер больше не нужен), у нас нет способа извлечь элемент из середины кучи.

Можно было бы использовать более сложную структуру данных (например, собственную кучу с поддержкой удаления по ключу), но есть элегантное решение проще.

### Решение: generation counter

Идея: каждый логический таймер имеет поле `_generation` (uint или int). При каждом перезапуске таймера `_generation` инкрементируется. Callback, помещённый в очередь, захватывает **текущее значение** generation в замыкании:

```csharp
private int _generation;

public void Restart(PriorityQueue<Action, double> queue, double fireAt)
{
    int captured = ++_generation;
    queue.Enqueue(() =>
    {
        if (captured != _generation) return; // stale — ignore
        OnTimerFired();
    }, fireAt);
}

public void Cancel()
{
    _generation++; // invalidate any pending callback
}
```

Когда callback срабатывает, он сравнивает захваченный `captured` с текущим `_generation`. Если они не совпадают — значит, таймер был перезапущен или отменён после постановки в очередь, и callback игнорируется (no-op).

### Где используется

Этот паттерн появляется дважды в нашем pipeline:

1. **RetransmissionController** — RTO watchdog. Перезапускается при каждом продвижении SND.UNA (новый ACK), при каждой ретрансляции (с удвоенным RTO), и отменяется, когда все данные подтверждены.

2. **DelayedAckPolicy** — deadline для отложенного ACK. Перезапускается при получении первого неподтверждённого сегмента, отменяется при отправке ACK по другой причине (gap detected, 2nd full segment).

Обратите внимание: callback остаётся в PriorityQueue навсегда (до момента dequeue). Это означает, что queue может содержать «мёртвые» элементы. На практике это не проблема: в типичном TCP-соединении количество таких элементов невелико, и они быстро извлекаются при продвижении виртуального времени. В production-системе, где таймеры могут жить часами, используются другие подходы (timer wheels в Linux, HashedWheelTimer в Netty).

---

## Часть 13.5 — InFlightSegment: что живёт в полёте

### Отличие от TcpSegment

В модуле 11 мы определили `TcpSegment` как `ref struct` — стековую структуру, которая существует только во время парсинга входящего кадра. Она содержит `ReadOnlySpan<byte>` на payload, что делает её невозможной для хранения в коллекциях (ref struct не может быть полем класса или элементом массива).

Для tracking неподтверждённых сегментов нам нужна другая структура — `InFlightSegment`. Это обычный класс, который:

- Владеет **копией данных** (byte[]), потому что оригинальный буфер мог быть переиспользован
- Хранит **время отправки** (SentAtTick) для вычисления RTT
- Отслеживает **факт ретрансляции** (WasRetransmitted) для алгоритма Karn

### InFlightSegment.cs

```csharp
namespace TcpReceivePipeline.Core;

/// <summary>
/// Сегмент, находящийся «в полёте» — отправленный, но ещё не подтверждённый.
/// В отличие от TcpSegment (ref struct для парсинга), это обычный класс,
/// который может жить в коллекциях произвольно долго.
/// </summary>
public sealed class InFlightSegment
{
    /// <summary>
    /// Sequence number первого байта payload.
    /// </summary>
    public uint SequenceNumber { get; }

    /// <summary>
    /// Копия данных сегмента. Это именно копия — оригинальный буфер
    /// мог быть возвращён в пул или перезаписан.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// Sequence number сразу после последнего байта payload.
    /// SequenceNumber + Data.Length (с учётом 32-bit wraparound).
    /// </summary>
    public uint EndSequence => unchecked(SequenceNumber + (uint)Data.Length);

    /// <summary>
    /// Виртуальное время первой (оригинальной) отправки.
    /// Используется для вычисления RTT sample при получении ACK.
    /// </summary>
    public double SentAtTick { get; }

    /// <summary>
    /// Был ли этот сегмент ретранслирован хотя бы один раз.
    /// Если true — RTT sample по ACK на этот сегмент НЕ берётся
    /// (алгоритм Karn, RFC 6298 S3).
    /// 
    /// Причина: если сегмент был отправлен дважды (оригинал + ретрансляция),
    /// мы не можем определить, на какую из двух отправок пришёл ACK.
    /// Использование такого неоднозначного измерения привело бы к искажению
    /// SRTT и, следовательно, к неправильному RTO.
    /// </summary>
    public bool WasRetransmitted { get; set; }

    public InFlightSegment(uint sequenceNumber, byte[] data, double sentAtTick)
    {
        SequenceNumber = sequenceNumber;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        SentAtTick = sentAtTick;
    }

    public override string ToString()
        => $"seq={SequenceNumber}..{EndSequence} sent@{SentAtTick:F3}"
         + (WasRetransmitted ? " [RETX]" : "");
}
```

Поле `WasRetransmitted` заслуживает особого внимания. Представьте ситуацию:

1. Сегмент S отправлен в момент t=1.0
2. ACK не получен, RTO срабатывает, S ретранслируется в t=4.0
3. ACK приходит в t=5.0

Какой RTT? 5.0 - 1.0 = 4.0? Или 5.0 - 4.0 = 1.0? Мы не знаем, потому что ACK не указывает, на какую копию сегмента он отвечает. Phil Karn в 1987 году предложил простое решение: **не использовать RTT sample вообще**, если сегмент был ретранслирован. Это и реализует флаг `WasRetransmitted`.

---

## Часть 13.6 — RetransmissionTimer: RFC 6298 (Jacobson/Karn)

### Формулы

RFC 6298 определяет алгоритм вычисления RTO (Retransmission Timeout) на основе измеренных значений RTT. Алгоритм использует экспоненциально взвешенное скользящее среднее (EWMA) с двумя переменными:

```
SRTT   — Smoothed RTT (сглаженное среднее)
RTTVAR — RTT Variance (вариация)
```

При получении нового измерения R:

```
Если это ПЕРВОЕ измерение:
    SRTT   = R
    RTTVAR = R / 2

Для последующих измерений:
    RTTVAR = (1 - beta) * RTTVAR + beta * |SRTT - R|     beta  = 1/4
    SRTT   = (1 - alpha) * SRTT + alpha * R               alpha = 1/8
    
ВАЖНО: RTTVAR вычисляется ДО обновления SRTT.

RTO = SRTT + max(G, 4 * RTTVAR)

где G — гранулярность таймера (clock granularity).
```

Константы alpha=1/8 и beta=1/4 были подобраны Van Jacobson эмпирически в 1988 году. Они обеспечивают хороший баланс между чувствительностью к изменениям и устойчивостью к шуму:

- alpha=1/8 означает, что новое измерение влияет на SRTT лишь на 12.5%. Старая история весит 87.5%.
- beta=1/4 означает, что вариация обновляется быстрее (25% от нового измерения), что делает RTO более чувствительным к джиттеру.

### RetransmissionTimer.cs

```csharp
namespace TcpReceivePipeline.Core;

/// <summary>
/// Вычисляет RTO по алгоритму Jacobson/Karn (RFC 6298).
/// 
/// Этот класс НЕ управляет таймерами — он только хранит состояние
/// SRTT/RTTVAR и вычисляет текущее значение RTO. Управление таймерами
/// (постановка в очередь, generation-счётчик) — ответственность
/// RetransmissionController.
/// </summary>
public sealed class RetransmissionTimer
{
    private const double Alpha = 1.0 / 8.0;   // коэффициент сглаживания SRTT
    private const double Beta  = 1.0 / 4.0;   // коэффициент сглаживания RTTVAR
    
    private readonly double _clockGranularity; // G — минимальная гранулярность
    private readonly double _minRto;           // нижняя граница RTO
    private readonly double _maxRto;           // верхняя граница RTO

    private double _srtt;    // Smoothed RTT
    private double _rttvar;  // RTT Variance
    private bool _hasFirstSample;
    private int _backoffCount;  // текущий уровень exponential backoff

    /// <summary>
    /// Текущее значение RTO с учётом backoff.
    /// </summary>
    public double Rto { get; private set; }

    /// <summary>
    /// Текущее значение SRTT (для отладки / логирования).
    /// </summary>
    public double Srtt => _srtt;

    /// <summary>
    /// Текущее значение RTTVAR (для отладки / логирования).
    /// </summary>
    public double Rttvar => _rttvar;

    /// <summary>
    /// Количество последовательных backoff-ов (для отладки).
    /// </summary>
    public int BackoffCount => _backoffCount;

    public RetransmissionTimer(
        double initialRto = 3.0,
        double clockGranularity = 0.001,
        double minRto = 1.0,
        double maxRto = 60.0)
    {
        _clockGranularity = clockGranularity;
        _minRto = minRto;
        _maxRto = maxRto;
        Rto = initialRto;
    }

    /// <summary>
    /// Обрабатывает новое измерение RTT.
    /// Вызывается только для сегментов, НЕ помеченных WasRetransmitted
    /// (алгоритм Karn — см. InFlightSegment).
    /// </summary>
    public void OnRttSample(double rtt)
    {
        if (rtt < 0) throw new ArgumentOutOfRangeException(nameof(rtt));

        if (!_hasFirstSample)
        {
            // RFC 6298 S2.2: первое измерение
            _srtt = rtt;
            _rttvar = rtt / 2.0;
            _hasFirstSample = true;
        }
        else
        {
            // RFC 6298 S2.3: последующие измерения
            // ВАЖНО: RTTVAR обновляется ДО SRTT
            _rttvar = (1.0 - Beta) * _rttvar + Beta * Math.Abs(_srtt - rtt);
            _srtt = (1.0 - Alpha) * _srtt + Alpha * rtt;
        }

        // RFC 6298 S2.3: RTO = SRTT + max(G, 4*RTTVAR)
        Rto = _srtt + Math.Max(_clockGranularity, 4.0 * _rttvar);

        // Clamping
        Rto = Math.Clamp(Rto, _minRto, _maxRto);

        // Новое измерение сбрасывает backoff
        _backoffCount = 0;
    }

    /// <summary>
    /// Exponential backoff: удваивает RTO при таймауте.
    /// RFC 6298 S5.5: «При каждом последовательном таймауте RTO удваивается».
    /// 
    /// Это часть алгоритма Karn: мы не обновляем SRTT/RTTVAR при таймауте,
    /// а просто удваиваем RTO. Только «чистое» RTT-измерение (не от
    /// ретранслированного сегмента) вернёт RTO к нормальным значениям.
    /// </summary>
    public void Backoff()
    {
        _backoffCount++;
        Rto = Math.Min(Rto * 2.0, _maxRto);
    }
}
```

Обратите внимание на разделение ответственности: `RetransmissionTimer` — это чистая математика. Он не знает о таймерах, очередях, сегментах или сети. Он принимает числа и выдаёт числа. Это делает его тривиальным для unit-тестирования:

```csharp
var timer = new RetransmissionTimer(initialRto: 3.0);
timer.OnRttSample(1.0);  // SRTT=1.0, RTTVAR=0.5, RTO=max(1.0, 1.0+max(G,2.0))=3.0
timer.OnRttSample(1.2);  // SRTT=1.025, RTTVAR=0.425, RTO=2.725 → clamped to minRto
```

---

## Часть 13.7 — RetransmissionController: очередь неподтверждённых сегментов

### Ответственность

`RetransmissionController` — самый сложный класс в этом модуле. Он координирует:

1. **Очередь in-flight сегментов** — все сегменты, отправленные но не подтверждённые
2. **Подсчёт duplicate ACK** — для trigger-а fast retransmit
3. **RTT sampling** — передача измерений в RetransmissionTimer с учётом алгоритма Karn
4. **RTO watchdog** — перезапуск таймера при продвижении SND.UNA

### Логика Duplicate ACK

RFC 5681 определяет duplicate ACK как ACK с тем же acknowledgment number, что и предыдущий. Наша реализация считает так:

- Первый ACK с данным ack number — это **baseline** (не дупликат)
- Каждый последующий ACK с тем же ack number — **duplicate** (инкрементирует счётчик)
- При достижении порога (3 duplicate ACK) — **fast retransmit**

Почему именно 3? Эмпирически: один дупликат может быть вызван переупорядочиванием в сети. Два — маловероятно, но возможно. Три — почти наверняка потеря.

### RetransmissionController.cs

```csharp
namespace TcpReceivePipeline.Core;

/// <summary>
/// Управляет ретрансляцией: отслеживает in-flight сегменты,
/// считает duplicate ACK, запускает RTO watchdog,
/// и выполняет fast retransmit при пороге duplicate ACK.
/// 
/// Делегаты:
///   retransmitAction — вызывается, когда нужно ретранслировать сегмент
///                      (RTO timeout или fast retransmit)
///   scheduleTimer    — планирует callback в очереди виртуального времени
///   getCurrentTime   — возвращает текущее виртуальное время
/// </summary>
public sealed class RetransmissionController
{
    private readonly List<InFlightSegment> _inFlight = new();
    private readonly RetransmissionTimer _rttEstimator;
    private readonly Action<InFlightSegment> _retransmitAction;
    private readonly Action<Action, double> _scheduleTimer;
    private readonly Func<double> _getCurrentTime;

    private uint _lastAckedSeq;
    private int _dupAckCount;
    private int _timerGeneration;

    /// <summary>Порог duplicate ACK для fast retransmit (RFC 5681).</summary>
    public int DupAckThreshold { get; }

    /// <summary>Текущий RTO (для логирования).</summary>
    public double CurrentRto => _rttEstimator.Rto;

    /// <summary>Количество сегментов в полёте.</summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>Текущий счётчик duplicate ACK (для логирования).</summary>
    public int DupAckCount => _dupAckCount;

    public RetransmissionController(
        RetransmissionTimer rttEstimator,
        Action<InFlightSegment> retransmitAction,
        Action<Action, double> scheduleTimer,
        Func<double> getCurrentTime,
        int dupAckThreshold = 3)
    {
        _rttEstimator = rttEstimator ?? throw new ArgumentNullException(nameof(rttEstimator));
        _retransmitAction = retransmitAction ?? throw new ArgumentNullException(nameof(retransmitAction));
        _scheduleTimer = scheduleTimer ?? throw new ArgumentNullException(nameof(scheduleTimer));
        _getCurrentTime = getCurrentTime ?? throw new ArgumentNullException(nameof(getCurrentTime));
        DupAckThreshold = dupAckThreshold;
    }

    /// <summary>
    /// Регистрирует отправленный сегмент в очереди in-flight.
    /// Если это первый сегмент (или первый после полного подтверждения),
    /// запускает RTO watchdog.
    /// </summary>
    public void OnSegmentSent(InFlightSegment segment)
    {
        _inFlight.Add(segment);

        // Запускаем RTO watchdog при первом неподтверждённом сегменте
        if (_inFlight.Count == 1)
        {
            ArmRtoWatchdog();
        }
    }

    /// <summary>
    /// Обрабатывает входящий ACK. Возвращает количество байт,
    /// подтверждённых этим ACK (0 = duplicate или невалидный).
    /// 
    /// Основная логика:
    /// 1. Если ackNumber > _lastAckedSeq — это "new ACK": удаляем
    ///    подтверждённые сегменты, берём RTT sample (Karn), обновляем watchdog.
    /// 2. Если ackNumber == _lastAckedSeq — это duplicate ACK:
    ///    инкрементируем счётчик, при пороге — fast retransmit.
    /// </summary>
    public uint OnAckReceived(uint ackNumber)
    {
        // --- NEW ACK ---
        if (TcpSequence.GreaterThan(ackNumber, _lastAckedSeq))
        {
            uint bytesAcked = unchecked(ackNumber - _lastAckedSeq);

            // RTT sampling с учётом алгоритма Karn:
            // берём sample только если хотя бы один подтверждённый сегмент
            // НЕ был ретранслирован.
            TakeRttSampleIfClean(ackNumber);

            // Удаляем полностью подтверждённые сегменты
            _inFlight.RemoveAll(s =>
                !TcpSequence.GreaterThan(s.EndSequence, ackNumber));

            _lastAckedSeq = ackNumber;
            _dupAckCount = 0; // сброс счётчика дупликатов

            // Перезапускаем или останавливаем RTO watchdog
            if (_inFlight.Count > 0)
            {
                ArmRtoWatchdog();
            }
            else
            {
                // Все данные подтверждены — отменяем watchdog
                _timerGeneration++;
            }

            return bytesAcked;
        }

        // --- DUPLICATE ACK ---
        if (ackNumber == _lastAckedSeq && _inFlight.Count > 0)
        {
            _dupAckCount++;

            if (_dupAckCount == DupAckThreshold)
            {
                // Fast retransmit: ретранслируем первый неподтверждённый сегмент
                var oldest = _inFlight[0];
                oldest.WasRetransmitted = true;
                _retransmitAction(oldest);

                // Перезапускаем RTO watchdog (сегмент ретранслирован,
                // даём ему новый шанс с текущим RTO)
                ArmRtoWatchdog();
            }
        }

        return 0;
    }

    /// <summary>
    /// RTT sampling по алгоритму Karn.
    /// 
    /// Ищем среди подтверждённых сегментов те, что НЕ были ретранслированы.
    /// Если таких нет — пропускаем sample полностью (Karn's algorithm).
    /// 
    /// Логика: ACK подтверждает все данные до ackNumber. Если ЛЮБОЙ
    /// из подтверждённых сегментов был ретранслирован, мы не можем
    /// определить, на оригинал или ретрансляцию ответил ACK.
    /// Однако RFC 6298 говорит: достаточно найти хотя бы один
    /// «чистый» сегмент для sample. Мы идём дальше: если в наборе
    /// подтверждённых есть хотя бы один ретранслированный, мы
    /// отбрасываем sample целиком. Это более консервативно, но безопасно.
    /// </summary>
    private void TakeRttSampleIfClean(uint ackNumber)
    {
        bool anyRetransmitted = false;
        InFlightSegment? sampleCandidate = null;

        foreach (var seg in _inFlight)
        {
            // Этот сегмент подтверждён данным ACK?
            if (!TcpSequence.GreaterThan(seg.EndSequence, ackNumber))
            {
                if (seg.WasRetransmitted)
                {
                    anyRetransmitted = true;
                    break; // один ретранслированный — пропускаем весь sample
                }
                sampleCandidate = seg;
            }
        }

        if (!anyRetransmitted && sampleCandidate != null)
        {
            double rtt = _getCurrentTime() - sampleCandidate.SentAtTick;
            if (rtt > 0)
            {
                _rttEstimator.OnRttSample(rtt);
            }
        }
    }

    /// <summary>
    /// Устанавливает (или перезапускает) RTO watchdog.
    /// Использует generation-счётчик для отмены предыдущего таймера.
    /// </summary>
    private void ArmRtoWatchdog()
    {
        int captured = ++_timerGeneration;
        double fireAt = _getCurrentTime() + _rttEstimator.Rto;

        _scheduleTimer(() =>
        {
            if (captured != _timerGeneration) return; // stale — отмена

            OnRtoExpired();
        }, fireAt);
    }

    /// <summary>
    /// Вызывается при истечении RTO.
    /// RFC 6298 S5.4-5.6:
    ///   - Ретранслировать самый ранний неподтверждённый сегмент
    ///   - Выполнить backoff (RTO *= 2)
    ///   - Перезапустить watchdog с новым (удвоенным) RTO
    /// </summary>
    private void OnRtoExpired()
    {
        if (_inFlight.Count == 0) return;

        var oldest = _inFlight[0];
        oldest.WasRetransmitted = true;

        // Backoff: удваиваем RTO
        _rttEstimator.Backoff();

        // Ретрансляция
        _retransmitAction(oldest);

        // Перезапускаем watchdog с удвоенным RTO
        ArmRtoWatchdog();
    }
}
```

### Поток управления

Рассмотрим типичный сценарий:

1. **OnSegmentSent(seg1)** — seg1 добавлен в `_inFlight`, RTO watchdog armed на `now + RTO`
2. **OnSegmentSent(seg2)** — seg2 добавлен, watchdog не перезапускается (уже работает)
3. **OnAckReceived(ack=seg1.End)** — seg1 удалён, RTT sample взят, watchdog перезапущен (ещё есть seg2)
4. **OnAckReceived(ack=seg2.End)** — seg2 удалён, RTT sample взят, watchdog отменён (все подтверждены)

А теперь сценарий с потерей:

1. **OnSegmentSent(seg1)**, **OnSegmentSent(seg2)**, **OnSegmentSent(seg3)**
2. seg2 потерян в сети
3. **OnAckReceived(ack=seg1.End)** — seg1 подтверждён, watchdog перезапущен
4. **OnAckReceived(ack=seg1.End)** — dup ACK #1 (peer получил seg3, но ожидает seg2)
5. **OnAckReceived(ack=seg1.End)** — dup ACK #2
6. **OnAckReceived(ack=seg1.End)** — dup ACK #3 → **FAST RETRANSMIT seg2**

---

## Часть 13.8 — DelayedAckPolicy: RFC 1122

### Зачем откладывать ACK

Немедленное подтверждение каждого входящего сегмента генерирует поток мелких пакетов (40 байт: 20 IPv4 + 20 TCP, без payload). При высокой скорости передачи это удваивает число пакетов в сети. Delayed ACK решает проблему: вместо ACK на каждый сегмент мы подтверждаем каждый второй, или отправляем ACK по таймеру, если второй сегмент не пришёл вовремя.

### Правила (RFC 1122 S4.2.3.2 + RFC 5681 S3.2)

1. **Gap detected (out-of-order)** — отправить ACK немедленно. Это критически важно: duplicate ACK — единственный способ сообщить отправителю о потере до истечения RTO. Чем быстрее мы отправим 3 дупликата, тем быстрее сработает fast retransmit.

2. **Каждый второй full-size сегмент** — отправить ACK немедленно. «Full-size» = payload >= MSS. Два полноразмерных сегмента подряд означают, что передача идёт на полной скорости, и ACK нужен для поддержания self-clocking.

3. **Таймер** — если ни одно из вышеуказанных условий не сработало в течение maxDelay, отправить ACK принудительно. RFC 1122 рекомендует не более 500 мс. В реальных реализациях обычно 40-200 мс (Linux использует 40 мс по умолчанию).

### DelayedAckPolicy.cs

```csharp
namespace TcpReceivePipeline.Core;

/// <summary>
/// Реализует политику Delayed ACK (RFC 1122, RFC 5681).
/// 
/// Вызывающий код передаёт события (OnSegmentReceived), а политика
/// решает, нужно ли отправить ACK сейчас (возвращает true)
/// или можно подождать (возвращает false, но планирует deadline).
/// 
/// Делегаты:
///   sendAckAction — отправляет ACK немедленно
///   scheduleTimer — планирует callback в очереди виртуального времени
///   getCurrentTime — текущее виртуальное время
/// </summary>
public sealed class DelayedAckPolicy
{
    private readonly Action _sendAckAction;
    private readonly Action<Action, double> _scheduleTimer;
    private readonly Func<double> _getCurrentTime;
    private readonly double _maxDelay;

    private int _pendingSegments;  // количество неподтверждённых сегментов
    private int _deadlineGeneration;  // generation-счётчик для отмены deadline

    /// <summary>
    /// Количество сегментов, ожидающих подтверждения (для логирования).
    /// </summary>
    public int PendingSegments => _pendingSegments;

    public DelayedAckPolicy(
        Action sendAckAction,
        Action<Action, double> scheduleTimer,
        Func<double> getCurrentTime,
        double maxDelay = 0.200)  // 200 мс по умолчанию
    {
        _sendAckAction = sendAckAction ?? throw new ArgumentNullException(nameof(sendAckAction));
        _scheduleTimer = scheduleTimer ?? throw new ArgumentNullException(nameof(scheduleTimer));
        _getCurrentTime = getCurrentTime ?? throw new ArgumentNullException(nameof(getCurrentTime));
        _maxDelay = maxDelay;
    }

    /// <summary>
    /// Вызывается при получении сегмента с данными.
    /// 
    /// hasGap: true, если после вставки в реассемблер обнаружен
    /// пропуск (BufferedRangeCount > 1). Означает out-of-order delivery.
    /// 
    /// isFullSegment: true, если payload.Length >= MSS.
    /// 
    /// Возвращает true, если ACK был отправлен (немедленно).
    /// </summary>
    public bool OnSegmentReceived(bool hasGap, bool isFullSegment)
    {
        _pendingSegments++;

        // Правило 1: Gap → немедленный ACK
        // RFC 5681 S3.2: «A TCP receiver SHOULD send an immediate
        // duplicate ACK when an out-of-order segment arrives.»
        if (hasGap)
        {
            FlushAck();
            return true;
        }

        // Правило 2: Каждый 2-й full-size → немедленный ACK
        if (isFullSegment && _pendingSegments >= 2)
        {
            FlushAck();
            return true;
        }

        // Правило 3: Установить deadline, если ещё не установлен
        if (_pendingSegments == 1)
        {
            ArmDeadline();
        }

        return false;
    }

    /// <summary>
    /// Сбрасывает pending-счётчик и отменяет deadline.
    /// Вызывается извне, когда ACK отправлен по другой причине
    /// (например, в ответ на отправку данных — piggybacking).
    /// </summary>
    public void OnAckSent()
    {
        _pendingSegments = 0;
        _deadlineGeneration++; // отменяем запланированный deadline
    }

    private void FlushAck()
    {
        _sendAckAction();
        _pendingSegments = 0;
        _deadlineGeneration++; // отменяем deadline, если был
    }

    private void ArmDeadline()
    {
        int captured = ++_deadlineGeneration;
        double fireAt = _getCurrentTime() + _maxDelay;

        _scheduleTimer(() =>
        {
            if (captured != _deadlineGeneration) return; // stale — отменён

            // Deadline expired: принудительный ACK
            if (_pendingSegments > 0)
            {
                FlushAck();
            }
        }, fireAt);
    }
}
```

Обратите внимание на взаимодействие delayed ACK и fast retransmit. Когда получатель обнаруживает gap (out-of-order сегмент), он отправляет ACK **немедленно**, не дожидаясь ни второго сегмента, ни таймера. Это обеспечивает быструю генерацию duplicate ACK, которые нужны отправителю для fast retransmit.

Если бы delayed ACK применялся и к out-of-order сегментам, отправитель мог бы ждать до 3 * maxDelay прежде чем получить 3 дупликата — за это время RTO бы уже сработал, и преимущество fast retransmit было бы потеряно.

---

## Часть 13.9 — CongestionController: RFC 5681

### Назначение

Congestion control ограничивает количество данных, которые отправитель может иметь в полёте одновременно. Без него отправитель мог бы отправить столько данных, сколько позволяет advertised window получателя, — а это может быть 64 КБ (или больше с Window Scale). Если промежуточный маршрутизатор имеет меньшую пропускную способность, его буфер переполнится, и начнутся массовые потери.

Congestion window (cwnd) — это **оценка пропускной способности сети**, которую отправитель поддерживает локально. В отличие от advertised window (rwnd), который явно сообщается получателем, cwnd вычисляется имплицитно на основе наблюдений за потерями и временем доставки.

### Три режима

RFC 5681 определяет два основных режима (slow start и congestion avoidance) и два реактивных механизма при обнаружении потерь:

**Slow Start** (cwnd < ssthresh):
```
На каждый новый ACK: cwnd += MSS

Это даёт экспоненциальный рост: за каждый RTT cwnd удваивается.
Название "slow start" — историческое: он «медленный» по сравнению
с отсутствием congestion control (отправить всё сразу), но быстрый
в абсолютном смысле.
```

**Congestion Avoidance** (cwnd >= ssthresh):
```
На каждый новый ACK: cwnd += MSS * MSS / cwnd

Это даёт линейный рост: за каждый RTT cwnd увеличивается на ~1 MSS.
Идея: мы приблизились к порогу насыщения, дальше нужно «прощупывать»
пропускную способность осторожно.
```

**Loss via 3 Duplicate ACK** (fast retransmit):
```
ssthresh = max(bytesInFlight / 2, 2 * MSS)
cwnd = ssthresh + 3 * MSS

"+3*MSS" — потому что 3 duplicate ACK означают, что 3 сегмента
после потерянного были доставлены (peer их получил и буферизировал).
Эти 3 сегмента уже «покинули сеть» и не создают нагрузки.
```

**Loss via RTO** (timeout):
```
ssthresh = max(bytesInFlight / 2, 2 * MSS)
cwnd = MSS  (полный сброс!)

RTO — более серьёзный сигнал, чем 3 dup ACK. При fast retransmit
мы знаем, что peer жив (он отправляет ACK), просто один сегмент
потерялся. При RTO мы НЕ ЗНАЕМ состояние сети: может быть,
маршрутизатор упал, может быть, линк разорван. Поэтому — полный
сброс до 1 MSS, начинаем slow start заново.
```

### Начальное окно

RFC 5681 S3.1 (обновлённое RFC 3390):
```
IW = min(4 * MSS, max(2 * MSS, 4380))
```

Для MSS=1460: IW = min(5840, max(2920, 4380)) = min(5840, 4380) = 4380 байт, что даёт 3 сегмента. На практике Linux с ядра 3.0+ использует IW=10*MSS (RFC 6928).

### CongestionController.cs

```csharp
namespace TcpReceivePipeline.Core;

/// <summary>
/// Реализует управление перегрузкой по RFC 5681 (New Reno-подобное).
/// 
/// Три режима:
///   1. Slow Start:     cwnd < ssthresh → cwnd += MSS per ACK
///   2. Cong. Avoidance: cwnd >= ssthresh → cwnd += MSS*MSS/cwnd per ACK
///   3. Loss response:  3 dup ACK или RTO → уменьшение cwnd
/// 
/// Этот класс НЕ управляет ретрансляцией и не отслеживает in-flight
/// сегменты. Он только предоставляет значение cwnd, которое
/// используется в формуле:
///   usable = min(cwnd, rwnd) - bytesInFlight
/// </summary>
public sealed class CongestionController
{
    private readonly int _mss;

    /// <summary>
    /// Congestion window — текущая оценка пропускной способности сети (в байтах).
    /// </summary>
    public int Cwnd { get; private set; }

    /// <summary>
    /// Slow Start Threshold — граница между slow start и congestion avoidance.
    /// При cwnd < ssthresh — slow start (экспоненциальный рост).
    /// При cwnd >= ssthresh — congestion avoidance (линейный рост).
    /// </summary>
    public int Ssthresh { get; private set; }

    /// <summary>
    /// MSS (Maximum Segment Size) — для вычислений.
    /// </summary>
    public int Mss => _mss;

    /// <summary>
    /// Текущий режим (для логирования).
    /// </summary>
    public string Mode => Cwnd < Ssthresh ? "SlowStart" : "CongAvoid";

    public CongestionController(int mss = 1460)
    {
        _mss = mss;

        // RFC 5681 S3.1: Initial Window
        // IW = min(4*MSS, max(2*MSS, 4380))
        Cwnd = Math.Min(4 * _mss, Math.Max(2 * _mss, 4380));

        // Начальный ssthresh — «бесконечность» (RFC 5681 S3.1)
        // На практике используем int.MaxValue; первая потеря установит
        // реальное значение.
        Ssthresh = int.MaxValue;
    }

    /// <summary>
    /// Вызывается при получении нового ACK (подтверждающего новые данные).
    /// 
    /// В slow start: cwnd += MSS (экспоненциальный рост).
    /// В congestion avoidance: cwnd += MSS*MSS/cwnd (линейный рост).
    /// </summary>
    public void OnNewAck()
    {
        if (Cwnd < Ssthresh)
        {
            // Slow Start: cwnd += MSS
            Cwnd += _mss;
        }
        else
        {
            // Congestion Avoidance: cwnd += MSS * MSS / cwnd
            // Это аппроксимация «увеличить cwnd на 1 MSS за RTT».
            // За один RTT приходит ~cwnd/MSS ACK-ов, каждый добавляет
            // MSS*MSS/cwnd, суммарно: (cwnd/MSS) * (MSS*MSS/cwnd) = MSS.
            int increment = (_mss * _mss) / Cwnd;
            if (increment < 1) increment = 1; // минимум 1 байт
            Cwnd += increment;
        }
    }

    /// <summary>
    /// Вызывается при обнаружении потери через 3 duplicate ACK (fast retransmit).
    /// 
    /// RFC 5681 S3.1:
    ///   ssthresh = max(FlightSize / 2, 2 * MSS)
    ///   cwnd = ssthresh + 3 * MSS
    /// 
    /// «+3*MSS» — компенсация за 3 сегмента, о которых мы знаем,
    /// что они были доставлены (peer послал 3 dup ACK, значит,
    /// 3 сегмента после потерянного дошли и буферизированы).
    /// </summary>
    public void OnTripleDupAck(int bytesInFlight)
    {
        Ssthresh = Math.Max(bytesInFlight / 2, 2 * _mss);
        Cwnd = Ssthresh + 3 * _mss;
    }

    /// <summary>
    /// Вызывается при RTO (retransmission timeout).
    /// 
    /// RFC 5681 S3.1:
    ///   ssthresh = max(FlightSize / 2, 2 * MSS)
    ///   cwnd = MSS  (полный сброс!)
    /// 
    /// RTO — самый жёсткий сигнал потери. В отличие от fast retransmit,
    /// где мы знаем, что peer жив (он отправляет ACK), при RTO мы
    /// НЕ ПОЛУЧИЛИ НИЧЕГО от peer. Возможные причины:
    ///   - Перегрузка настолько сильная, что маршрутизатор отбрасывает всё
    ///   - Маршрут изменился
    ///   - Линк разорван и восстанавливается
    /// Во всех случаях безопасная стратегия — начать с минимума.
    /// </summary>
    public void OnRtoExpired(int bytesInFlight)
    {
        Ssthresh = Math.Max(bytesInFlight / 2, 2 * _mss);
        Cwnd = _mss; // полный сброс до 1 MSS
    }

    /// <summary>
    /// Вызывается при выходе из fast recovery (все потерянные данные
    /// подтверждены). Сбрасывает cwnd до ssthresh (deflation).
    /// 
    /// Во время fast recovery cwnd «раздувается» (inflated) каждым
    /// дополнительным dup ACK: cwnd += MSS. Это позволяет продолжать
    /// отправку новых данных. При выходе из recovery мы «сдуваем»
    /// cwnd обратно до ssthresh, что соответствует нашей реальной
    /// оценке пропускной способности сети.
    /// </summary>
    public void OnRecoveryExit()
    {
        Cwnd = Ssthresh;
    }
}
```

### Формула usable window с congestion control

До введения congestion control:
```
usable = rwnd - bytesInFlight
```

После:
```
usable = min(cwnd, rwnd) - bytesInFlight
```

Это единственное изменение в data path. Congestion control не модифицирует сегменты, не меняет ACK, не влияет на реассемблер. Он просто ограничивает **скорость** отправки, уменьшая количество данных, которые могут находиться в полёте одновременно.

На первый взгляд это кажется тривиальным — одна строчка `Math.Min()`. Но эффект огромен: без этой строчки TCP-соединение на трансконтинентальном линке с 100 Мбит/с и 200 мс RTT заполнит все буферы на пути, вызовет массовую потерю пакетов и рухнет до скорости <1 Мбит/с. С congestion control оно стабильно работает на полной скорости.

---

## Часть 13.10 — Прогон 1: Delayed ACK и Fast Retransmit (без cwnd)

### Сценарий

Для первого прогона мы отключаем congestion control (cwnd = infinity) и концентрируемся на взаимодействии delayed ACK, RTO и fast retransmit.

Условия:

- 6 сегментов по 4 байта: «ABCD», «EFGH», «IJKL», «MNOP», «QRST», «UVWX»
- Sequence numbers: 1001-1004, 1005-1008, 1009-1012, 1013-1016, 1017-1020, 1021-1024
- Сегмент #2 («EFGH», seq=1005) **потерян** в сети
- Все остальные доставлены с задержкой 1.0 тик (network latency)
- Интервал между отправками: 0.01 тик (имитация burst)
- Initial RTO: 3.0
- Delayed ACK maxDelay: 1.5

### Полная трассировка

```
[t=0.00] SEND seq=1001 len=4 "ABCD"
[t=0.01] SEND seq=1005 len=4 "EFGH"   ← будет потерян
[t=0.02] SEND seq=1009 len=4 "IJKL"
[t=0.03] SEND seq=1013 len=4 "MNOP"
[t=0.04] SEND seq=1017 len=4 "QRST"
[t=0.05] SEND seq=1021 len=4 "UVWX"
         RTO watchdog armed at t=0.00+3.0=3.0

--- Первый сегмент доставлен ---
[t=1.00] DELIVER seq=1001 "ABCD"
         Reassembler: RcvNxt=1005, ranges=1, no gap
         DelayedACK: pendingSegments=1, arm deadline at t=1.00+1.5=2.5
         (ACK задержан — нет gap, это первый сегмент)

--- Сегмент #2 потерян, #3 приходит out-of-order ---
[t=1.02] DELIVER seq=1009 "IJKL"
         Reassembler: RcvNxt=1005 (не продвинулся!), ranges=2, gap at 1005-1008
         DelayedACK: gap detected → IMMEDIATE ACK
         >>> ACK ack=1005 win=... (первый ACK за 1005)

         Peer receives ACK at t=1.02:
           RTT sample: 1.02 - 0.00 = 1.02 (от seg1, первый не-retx сегмент)
           SRTT=1.02, RTTVAR=0.51, RTO=1.02+max(G,2.04)=3.06
           RTO watchdog rearmed at t=1.02+3.06=4.08

--- Сегмент #4 ---
[t=1.03] DELIVER seq=1013 "MNOP"
         Reassembler: RcvNxt=1005, ranges=3, gap still at 1005
         DelayedACK: gap → IMMEDIATE ACK
         >>> ACK ack=1005 (dup ACK #1)

--- Сегмент #5 ---
[t=1.04] DELIVER seq=1017 "QRST"
         Reassembler: RcvNxt=1005, ranges=4, gap at 1005
         DelayedACK: gap → IMMEDIATE ACK
         >>> ACK ack=1005 (dup ACK #2)

--- Сегмент #6 ---
[t=1.05] DELIVER seq=1021 "UVWX"
         Reassembler: RcvNxt=1005, ranges=5, gap at 1005
         DelayedACK: gap → IMMEDIATE ACK
         >>> ACK ack=1005 (dup ACK #3!)

         RetransmissionController: dupAckCount == 3
         >>> FAST RETRANSMIT seq=1005 "EFGH"
         (RTO watchdog was armed at 4.08 — fast retransmit fires
          at t=1.05, то есть за 3.03 тика ДО RTO!)

--- Ретранслированный сегмент доставлен ---
[t=2.05] DELIVER seq=1005 "EFGH" (ретрансляция)
         Reassembler: КАСКАДНОЕ СРАБАТЫВАНИЕ!
           RcvNxt: 1005 → 1009 → 1013 → 1017 → 1021 → 1025
           5 диапазонов склеены в один непрерывный блок
         onDataReady: "EFGHIJKLMNOPQRSTUVWX" (20 байт)

         DelayedACK: pendingSegments=1, arm deadline at t=2.05+1.5=3.55
         (нет gap после реассемблинга — все данные непрерывны)

--- Delayed ACK deadline ---
[t=3.55] DelayedACK deadline fires
         >>> ACK ack=1025 win=...

         Peer receives ACK at t=3.55:
           Karn check: seg at seq=1005 WasRetransmitted=true
           → NO RTT SAMPLE (ambiguous — не знаем, оригинал или ретрансляция)
           RTO remains 3.06 (не обновлён)

=== Итог ===
Приложение получило: "ABCDEFGHIJKLMNOPQRSTUVWX" (24 байта)
Все данные доставлены в правильном порядке.
```

### Ключевой вывод

Fast retransmit сработал в момент t=1.05. RTO бы сработал в момент t=4.08. Разница — **3.03 тика**. Это и есть ответ на вопрос «зачем fast retransmit, если есть RTO?»:

- **RTO** — это «worst case fallback». Он сработает в любом случае, но поздно.
- **Fast retransmit** — это «early detection». Три duplicate ACK — сильный сигнал потери, и мы можем реагировать намного быстрее.

В реальных сетях RTT может быть 100-200 мс, а initial RTO — 1 секунда. Fast retransmit срабатывает за время ~1 RTT после потери (нужно время, чтобы последующие сегменты дошли и вернулись как dup ACK), а RTO — через 1+ секунд. Разница ощутима для пользователя.

Второй вывод: алгоритм Karn работает корректно. Финальный ACK (ack=1025) подтверждает набор сегментов, среди которых есть ретранслированный (seq=1005). RTT sample **не берётся**, потому что мы не можем определить, на какую копию сегмента (оригинал или ретрансляцию) ответил ACK. Если бы мы ошибочно взяли sample, это могло бы привести к заниженному SRTT и, как следствие, к слишком агрессивному RTO.

---

## Часть 13.11 — Прогон 2: Congestion Control

### Что меняется

В предыдущем прогоне мы не ограничивали отправку — все 6 сегментов ушли мгновенно (burst). Теперь подключаем `CongestionController` и смотрим, как cwnd влияет на порядок событий.

Параметры:

- MSS = 4 (для совпадения с размером наших тестовых сегментов)
- IW = min(4*4, max(2*4, 4380)) = min(16, max(8, 4380)) = min(16, 4380) = **16 байт** = 4 сегмента
- Отправить нужно 6 сегментов по 4 байта = 24 байта
- cwnd = 16, поэтому 5-й и 6-й сегменты ждут

### Формула отправки

```
usable = min(cwnd, rwnd) - bytesInFlight

До cwnd:   usable = rwnd - bytesInFlight = 65535 - 0 = 65535  → шлём всё
После cwnd: usable = min(16, 65535) - 0 = 16 → шлём 4 сегмента (16 байт)
```

### Полная трассировка

```
[t=0.000] cwnd=16 ssthresh=INF mode=SlowStart

[t=0.000] SEND seq=1001 "ABCD"  bytesInFlight=4  usable=12
[t=0.010] SEND seq=1005 "EFGH"  bytesInFlight=8  usable=8   ← потерян
[t=0.020] SEND seq=1009 "IJKL"  bytesInFlight=12 usable=4
[t=0.030] SEND seq=1013 "MNOP"  bytesInFlight=16 usable=0
          QRST и UVWX не могут быть отправлены — usable=0!
          RTO watchdog armed at t=0.000+3.0=3.0

--- Первый ACK ---
[t=1.000] DELIVER seq=1001 "ABCD" → ACK delayed
[t=1.020] DELIVER seq=1009 "IJKL" → gap! → ACK ack=1005
[t=1.030] DELIVER seq=1013 "MNOP" → gap! → ACK ack=1005 (dup #1)

--- ACK ack=1005 (first) прибывает к отправителю ---
[t=1.002] Peer receives ACK ack=1005
          RTT sample: 1.002 (от seq=1001, sent at 0.000)
          SRTT=1.002, RTTVAR=0.501, RTO=3.006
          bytesInFlight: 16→12 (seg1 подтверждён)
          cwnd += MSS = 16+4 = 20 (slow start)
          usable = min(20,65535)-12 = 8 → можем послать 2 сегмента!

[t=1.002] SEND seq=1017 "QRST"  bytesInFlight=16 usable=4
[t=1.012] SEND seq=1021 "UVWX"  bytesInFlight=20 usable=0

--- Duplicate ACK продолжают прибывать ---
[t=1.012] Peer receives ACK ack=1005 (dup #1) — bytesInFlight не меняется
[t=1.013] Peer receives ACK ack=1005 (dup #2)

--- Сегменты #5 и #6 доставляются peer ---
[t=2.002] DELIVER seq=1017 "QRST" → gap! → ACK ack=1005 (dup #2 от peer)
[t=2.012] DELIVER seq=1021 "UVWX" → gap! → ACK ack=1005 (dup #3 от peer)

--- Третий dup ACK приходит к отправителю ---
[t=2.003] Peer receives ACK ack=1005 (dup #3)
          RetransmissionController: dupAckCount == 3
          >>> FAST RETRANSMIT seq=1005 "EFGH"

          CongestionController.OnTripleDupAck(bytesInFlight=20):
            ssthresh = max(20/2, 2*4) = max(10, 8) = 10
            cwnd = 10 + 3*4 = 22

--- Ретрансляция доставлена ---
[t=3.003] DELIVER seq=1005 "EFGH"
          Reassembler: каскадное срабатывание
          RcvNxt: 1005 → 1025 (все 5 диапазонов объединены)
          onDataReady: "EFGHIJKLMNOPQRSTUVWX"

[t=4.503] ACK ack=1025 (delayed ACK fires)
          Peer receives ACK:
            Karn: seq=1005 retransmitted → NO RTT sample
            bytesInFlight: 20→0 (всё подтверждено)
            CongestionController.OnRecoveryExit():
              cwnd = ssthresh = 10 (deflation)
            RTO watchdog cancelled (nothing in flight)

=== Итог ===
cwnd: 16 → 20 → 22 → 10
ssthresh: INF → 10
mode: SlowStart → CongAvoid (cwnd=10 >= ssthresh=10)
```

### Ключевые отличия от прогона 1

1. **Сегменты 5-6 не отправлены сразу.** При cwnd=16 (4 сегмента по 4 байта) usable window исчерпывается после 4-го сегмента. QRST и UVWX ждут до t=1.002, когда первый ACK освобождает место в окне и slow start увеличивает cwnd до 20.

2. **Duplicate ACK приходят позже.** Поскольку seg5 и seg6 отправлены позже (t=1.002 и t=1.012), они доставляются peer позже, и 3-й dup ACK приходит к отправителю в t=2.003 вместо t=1.05.

3. **Fast retransmit всё ещё быстрее RTO.** RTO watchdog был перезапущен при первом ACK (t=1.002+3.006=4.008). Fast retransmit сработал в t=2.003 — разница ~2 тика.

4. **Cwnd после recovery = 10.** До потери cwnd=20 (после slow start). После recovery cwnd сжимается до ssthresh=10 — вдвое меньше пикового значения. Следующий раз рост будет линейным (congestion avoidance), не экспоненциальным.

Это и есть суть congestion control: **он навязывает темп отправки**. Без него все 6 сегментов уходят мгновенно. С ним — первые 4 уходят сразу, а оставшиеся 2 ждут подтверждения. В реальной сети с bandwidth 100 Мбит/с и RTT 20 мс это разница между «заполнить все буферы на пути» и «плавно увеличивать скорость до тех пор, пока сеть справляется».

---

## Часть 13.12 — Две ошибки, найденные трассировкой

### Ошибка 1: PriorityQueue не гарантирует порядок при равных приоритетах

**Симптом:** при отправке всех 6 сегментов с одинаковым временем (t=0), их delivery events тоже имели одинаковое время (t=1.0). PriorityQueue извлекала их в произвольном порядке — иногда seg3 перед seg1.

**Причина:** `PriorityQueue<T, TPriority>` в .NET реализован как min-heap, который не гарантирует стабильность (FIFO) среди элементов с одинаковым приоритетом. Это документированное поведение, а не баг. Min-heap оперирует только сравнением приоритетов; элементы с одинаковым приоритетом могут извлекаться в любом порядке.

**Решение:** добавить искусственное смещение (offset) к времени отправки каждого сегмента:

```csharp
for (int i = 0; i < segments.Count; i++)
{
    double sendTime = 0.0 + i * 0.01;  // 0.00, 0.01, 0.02, ...
    // schedule delivery at sendTime + networkLatency
}
```

Смещение 0.01 тика между сегментами достаточно, чтобы PriorityQueue различала их, но не влияет на логику теста. В реальной сети пакеты уходят не мгновенно — между ними проходит время serialization delay (время передачи кадра на физический уровень), которое для 1500-байтного кадра на 1 Гбит/с составляет ~12 мкс.

### Ошибка 2: взаимодействие Delayed ACK и RTO

**Симптом:** при maxDelay=3.0 (исходное значение для тестирования) финальный ACK (после реассемблинга всех данных) отправлялся в t=2.05+3.0=5.05. Но RTO watchdog после fast retransmit был перезапущен с удвоенным RTO... и в определённых конфигурациях мог сработать до прихода delayed ACK, вызывая **ложную ретрансляцию** (spurious retransmission).

**Причина:** конфликт между двумя таймерами:

```
Delayed ACK deadline: t=2.05 + maxDelay
RTO watchdog:         t=1.05 + currentRTO (≈3.06 после первого sample)
```

При maxDelay=3.0: deadline=5.05, RTO fires at ~4.11. RTO < deadline → ложная ретрансляция!

При maxDelay=1.5: deadline=3.55, RTO fires at ~4.11. deadline < RTO → ACK приходит вовремя.

**Решение:** уменьшить maxDelay до 1.5 (или, в general case, обеспечить maxDelay < RTO). Это реальная проблема, а не артефакт нашей реализации. В production TCP-стеках Delayed ACK maxDelay обычно 40-200 мс, а minimum RTO — 200 мс-1 секунда. Именно поэтому RFC 1122 рекомендует maxDelay не более 500 мс, а RFC 6298 устанавливает minimum RTO = 1 секунда.

Эта ошибка — хороший пример того, зачем нужна трассировка. Без детальной визуализации потока событий было бы крайне трудно определить, что delayed ACK «конкурирует» с RTO watchdog. Трассировка сделала проблему очевидной: два таймера, два deadline, один приходит раньше другого — порядок имеет значение.

---

## Часть 13.13 — Production Corner

### Что мы реализовали

Наш pipeline реализует фундаментальную архитектуру TCP:

- **Receive path:** parse → validate → reassemble → deliver
- **Feedback loop:** ACK generation (delayed/immediate) → retransmission control → congestion control
- **Loss detection:** RTO (timeout) + fast retransmit (3 dup ACK)
- **Rate control:** cwnd + rwnd → usable window

Это работающая модель, которая корректно обрабатывает потери, out-of-order доставку, ретрансляцию и congestion control.

### Что мы не реализовали

Для полноты картины перечислим, что есть в production TCP-стеках (например, Linux `tcp_input.c`, ~6000 строк) и чего нет в нашей реализации:

**RACK-TLP (RFC 8985)** — Recent ACKnowledgment and Tail Loss Probe. Современная альтернатива duplicate ACK-based loss detection. Использует временные метки для определения потерь: если сегмент отправлен давно и его «соседи» уже подтверждены, он считается потерянным. TLP (Tail Loss Probe) отправляет probe-сегмент при подозрении, что хвост передачи потерян — ситуация, когда duplicate ACK не приходят, потому что после потерянного сегмента не было отправлено ничего.

**NewReno partial ACK (RFC 6582)** — обработка ACK, который подтверждает часть данных, потерянных во время fast recovery. Наша реализация не различает partial и full ACK в recovery — она либо полностью выходит из recovery, либо нет.

**BBR / CUBIC** — алгоритмы congestion control, используемые в production. CUBIC (по умолчанию в Linux) использует кубическую функцию для recovery cwnd после потери, что обеспечивает лучшую утилизацию высокоскоростных линков. BBR (разработан Google) моделирует bottleneck bandwidth и RTT отдельно, вместо того чтобы реагировать на потери. Мы реализовали базовый RFC 5681 (Reno-подобный), который является отправной точкой для понимания всех остальных алгоритмов.

**Pacing** — распределение отправки во времени. Наша реализация отправляет весь usable window мгновенно (burst). В production TCP (особенно BBR) пакеты «размазываются» во времени, чтобы не создавать microburst на промежуточных маршрутизаторах.

**Window Scale (RFC 7323)** — расширение максимального window до 1 ГБ (вместо 65535 байт). Необходимо для высокоскоростных линков с большим BDP. Наша реализация ограничена ushort (65535), что достаточно для учебных целей, но недостаточно для production.

**SACK-based recovery (RFC 2018, RFC 6675)** — Selective ACK позволяет получателю сообщить, какие **именно** диапазоны байт он получил, а не только RcvNxt. Это радикально улучшает recovery при множественных потерях: отправитель знает, что ретранслировать, а что уже доставлено. Без SACK отправитель вынужден угадывать — или ретранслировать всё после точки потери (go-back-N), или надеяться, что потерян только один сегмент (как в нашей реализации).

**Timestamps (RFC 7323)** — позволяют измерять RTT для каждого ACK, а не только для «чистых» (non-retransmitted) сегментов. С timestamps алгоритм Karn становится ненужным: timestamp в ACK однозначно указывает, на какую отправку он отвечает.

**ECN (Explicit Congestion Notification, RFC 3168)** — маршрутизатор помечает пакет битом CE (Congestion Experienced) вместо того, чтобы отбрасывать его. Получатель сообщает отправителю через ECE-флаг в ACK. Отправитель реагирует как при потере, но без фактической потери данных. Это позволяет congestion control реагировать на перегрузку до того, как буферы переполнятся.

### Что реализация делает правильно

Несмотря на перечисленные упрощения, наша реализация корректно моделирует **фундаментальный замкнутый контур** TCP:

```
Отправить данные
    ↓
Ждать ACK
    ↓
Получить ACK (или timeout)
    ↓
Обновить оценку сети (RTT, cwnd)
    ↓
Отправить следующую порцию (не больше usable window)
    ↓
(цикл повторяется)
```

Именно этот контур является сердцем TCP уже более 35 лет — с момента, когда Van Jacobson в 1988 году добавил congestion control в 4.3BSD. BBR, CUBIC, RACK — это оптимизации внутри этого контура, а не замена.

---

## Часть 13.14 — Что дальше: от учебного стека к production

### Эволюционная карта

За 13 модулей мы прошли путь от физического уровня до полного приёмного конвейера TCP:

```
Модуль 1:  Ethernet Parser (физический уровень и кадры)
              |
              v
Модуль 2:  IPv4 Parser + Reassembly (сетевой уровень)
              |
              v
Модуль 11: TCP Header Parser (парсинг заголовка)
              |
              v
Модуль 11: Sliding Window (SND.UNA/NXT/WND, RCV.NXT/WND)
              |
              v
Модуль 11: Segment Acceptability (RFC 9293 S3.4)
              |
              v
Модуль 12: TCP Stream Reassembler (OOO buffering + in-order delivery)
              |
              v
Модуль 13: Receive Pipeline (интеграция всех компонентов)
              |
              v
Модуль 13: Delayed ACK (RFC 1122 — таймер + gap detection)
              |
              v
Модуль 13: Retransmission Timer (RFC 6298 — Jacobson/Karn)
              |
              v
Модуль 13: Fast Retransmit (RFC 5681 — 3 dup ACK)
              |
              v
Модуль 13: Congestion Control (RFC 5681 — slow start + cong. avoidance)
              |
              v
           HTTP поверх нашего TCP
```

Каждый уровень в этой иерархии опирается на предыдущие. TCP Header Parser не может работать без IPv4 Parser (нужен pseudo-header для checksum). Sliding Window не имеет смысла без парсера (нужны поля seq/ack/win). Reassembler использует sliding window для определения допустимости сегментов. Delayed ACK зависит от реассемблера (нужно знать, есть ли gap). Fast retransmit зависит от duplicate ACK, которые генерирует delayed ACK policy. Congestion control зависит от RTT measurements, которые берёт retransmission controller.

Это не случайность — это **архитектура**. Каждый компонент решает ровно одну задачу и предоставляет чёткий интерфейс следующему. Именно поэтому мы могли разрабатывать и тестировать их независимо в модулях 11-12, а затем собрать вместе в модуле 13.

### Путь к production

Чтобы превратить наш учебный стек в production-пригодную реализацию, нужно пройти ещё несколько этапов:

1. **Полная машина состояний TCP** — SYN/SYN-ACK/FIN/RST, graceful shutdown, TIME_WAIT, simultaneous open/close. Наш pipeline работает только в состоянии ESTABLISHED.

2. **SACK** — без selective acknowledgment recovery при множественных потерях неэффективна. Добавление SACK радикально изменяет RetransmissionController: вместо «ретранслировать первый неподтверждённый» он должен «ретранслировать все сегменты, не покрытые SACK-блоками».

3. **Window Scale + Timestamps** — для работы на линках с большим BDP (BDP > 65535 байт, что выполняется уже при bandwidth > 5 Мбит/с и RTT > 100 мс).

4. **Современный congestion control** — CUBIC или BBR вместо базового RFC 5681.

5. **Реальный сетевой ввод-вывод** — замена виртуального времени на реальные таймеры, PriorityQueue на timer wheel, массивов на ring buffer с zero-copy.

Но фундамент — тот, что мы построили в этих трёх модулях — остаётся неизменным. Все production-оптимизации — это вариации внутри той же архитектуры: parse → validate → reassemble → ACK → adjust window → send.

---

*[Предыдущий модуль: Модуль 12 — Out-of-Order Reassembly](Module-12-Out-of-Order-Reassembly.md)*
