# Модуль 11: Sliding Window — Реализация на C#

*«Первый шаг к собственному TCP-стеку: учимся считать байты»*

> **Исходный код:** `src/TcpSlidingWindow/`
> **Запуск:** `dotnet run --project src/TcpSlidingWindow`
> **Предыдущий модуль:** [Модуль 10: EVE-NG Lab](Module-10-EVE-NG-Lab.md)
> **Следующий модуль:** [Модуль 12: Out-of-Order Reassembly](Module-12-Out-of-Order-Reassembly.md)

---

# Часть V: TCP на практике — собираем свой стек на C#

Модули 1--10 были теорией: мы разобрали конечный автомат TCP, congestion control, bufferbloat, Window Scaling, SACK, QUIC, стеки Windows и Linux, развернули лабораторный стенд. Начиная с этого модуля мы переходим к **практике** — пишем собственную мини-реализацию TCP-стека на C# / .NET 9.

Зачем? Не для того, чтобы заменить `System.Net.Sockets`. А потому что единственный способ по-настоящему понять, как работают SND.UNA, RCV.NXT, segment acceptability test и retransmission timer — это написать их руками. Когда вы сами столкнётесь с тем, что `a < b` ломается при переполнении 32-битного sequence number, вы запомните это на всю жизнь.

План на часть V:

| Модуль | Что делаем |
|---|---|
| **11 — Sliding Window** | SND/RCV переменные, арифметика по модулю 2^32, segment acceptability |
| 12 — Out-of-Order Reassembly | Буфер переупорядочивания на `SortedList<uint, Segment>` |
| 13 — Congestion Control | AIMD + Slow Start, разделение SND и RCV, TcpSendWindow + TcpReceiveEndpoint |
| 14 — Retransmission Timer | Jacobson/Karn RTO, экспоненциальный backoff |
| 15 — Интеграция | Соединяем всё вместе, гоняем трафик через raw sockets |

Каждый модуль содержит полные исходники всех `.cs`-файлов. Вы можете скопировать код и запустить его — никаких «упражнений для читателя».

---

## Часть 11.1: О чём этот модуль

### Цель

В этом модуле мы реализуем **sliding window** — механизм, который позволяет TCP-отправителю передать несколько сегментов без ожидания ACK на каждый, а получателю — контролировать поток данных через размер окна.

Конкретно мы реализуем:

1. **Арифметику sequence numbers** — корректное сравнение 32-битных чисел с учётом переполнения (wrap-around).
2. **Zero-alloc парсинг TCP-заголовка** — `readonly ref struct TcpSegment`, который живёт на стеке и работает с `Span<byte>`.
3. **SlidingWindowTracker** — класс, который хранит переменные скользящего окна (SND.UNA, SND.NXT, SND.WND, RCV.NXT, RCV.WND) и реализует segment acceptability test из RFC 9293 S3.4.

### Переменные скользящего окна

RFC 9293 (S3.3.1) определяет следующие переменные для стороны отправителя (**send sequence space**):

```
SND.UNA  — Oldest unacknowledged sequence number
SND.NXT  — Next sequence number to send
SND.WND  — Send window (сколько байт peer готов принять)
ISS      — Initial send sequence number
```

И для стороны получателя (**receive sequence space**):

```
RCV.NXT  — Next expected sequence number
RCV.WND  — Receive window (сколько байт мы готовы принять)
IRS      — Initial receive sequence number
```

Визуально send sequence space выглядит так:

```
         1         2          3          4
    ----------|----------|----------|----------
           SND.UNA    SND.NXT    SND.UNA
                                +SND.WND

  1 — Уже отправлено и подтверждено (acknowledged)
  2 — Отправлено, но ещё не подтверждено (in-flight)
  3 — Можно отправить прямо сейчас (usable window)
  4 — Нельзя отправить: за пределами окна
```

Область 2 + 3 — это и есть **скользящее окно**. Когда приходит ACK, SND.UNA сдвигается вправо, «открывая» правый край окна. Когда мы отправляем данные, SND.NXT сдвигается вправо, «закрывая» левый край usable window.

Для receive sequence space:

```
         1          2          3
    ----------|----------|----------
           RCV.NXT    RCV.NXT
                      +RCV.WND

  1 — Уже принято и подтверждено
  2 — Можно принять (receive window)
  3 — За пределами окна — отбросить
```

Ключевое наблюдение: все эти переменные — 32-битные unsigned integers, и они **переполняются**. Sequence number 4 294 967 295 (0xFFFFFFFF) + 1 = 0. Обычная операция `<` в этом месте даёт неправильный результат. Именно с этой проблемы мы начнём.

---

## Часть 11.2: Арифметика по модулю 2^32

### Проблема

Sequence numbers в TCP — это 32-битные unsigned integers. Они начинаются со случайного ISN (см. Модуль 3, Часть 3.2) и монотонно растут. Рано или поздно они переполняются:

```
4294967293   (0xFFFF_FFFD)
4294967294   (0xFFFF_FFFE)
4294967295   (0xFFFF_FFFF)
0            (0x0000_0000)  <- здесь наивное a < b ломается
1            (0x0000_0001)
2            (0x0000_0002)
```

Если `a = 4294967295` и `b = 1`, то наивная проверка `a < b` вернёт `false` — потому что 4294967295 явно больше 1. Но в контексте TCP sequence numbers `b` идёт **после** `a` — следовательно, `a < b` должно быть `true`.

На 10 Gbps линке полный оборот sequence space занимает:

```
  4 294 967 296 байт / 1 250 000 000 байт/с ≈ 3.4 секунды
```

То есть это не теоретическая проблема — на быстрых линках переполнение происходит каждые несколько секунд.

### Решение: знаковое вычитание

RFC 9293 не указывает конкретный алгоритм, но де-факто стандартный приём — интерпретировать разность как знаковое (signed) 32-битное число:

```csharp
public static bool LessThan(uint a, uint b) => unchecked((int)(a - b)) < 0;
```

Как это работает? Разберём по шагам.

**Случай 1: обычный порядок, `a = 100`, `b = 200`**

```
  a - b = 100 - 200 = 4294967096 (как uint)
  (int)(4294967096) = -200
  -200 < 0 → true ✓
```

**Случай 2: a > b (a идёт после b), `a = 200`, `b = 100`**

```
  a - b = 200 - 100 = 100 (как uint)
  (int)(100) = 100
  100 < 0 → false ✓
```

**Случай 3: wrap-around, `a = 0xFFFFFFFF`, `b = 1`**

```
  a - b = 0xFFFFFFFF - 1 = 0xFFFFFFFE = 4294967294 (как uint)
  (int)(4294967294) = -2
  -2 < 0 → true ✓   (a действительно «раньше» b)
```

**Случай 4: wrap-around, `a = 1`, `b = 0xFFFFFFFF`**

```
  a - b = 1 - 0xFFFFFFFF = 2 (как uint, с переполнением)
  (int)(2) = 2
  2 < 0 → false ✓   (a действительно «позже» b)
```

Трюк работает, **пока расстояние между `a` и `b` не превышает 2^31 (2 147 483 648)**. При sequence space размером 4 ГБ это означает, что окно не может быть больше 2 ГБ — что заведомо выполняется для реального TCP (максимальное окно с Window Scaling — 1 ГБ, см. RFC 7323).

Ключевое слово `unchecked` в C# отключает проверку арифметического переполнения, которая в checked-контексте выбросила бы `OverflowException`. Здесь переполнение — не ошибка, а часть алгоритма.

### Полный класс: SequenceMath.cs

```csharp
// src/TcpSlidingWindow/Core/SequenceMath.cs

namespace TcpSlidingWindow.Core;

/// <summary>
/// Арифметика TCP sequence numbers (unsigned 32-bit) с учётом wrap-around.
/// 
/// Корректность гарантируется при условии, что расстояние между
/// сравниваемыми номерами не превышает 2^31 (половина sequence space).
/// Для реального TCP это всегда выполняется: максимальное окно = 1 ГБ (RFC 7323),
/// что значительно меньше 2 ГБ.
/// 
/// Приём описан в RFC 1982 (Serial Number Arithmetic) и используется
/// во всех реализациях TCP — от Linux (before()/after() в tcp.h)
/// до Windows (tcpip.sys) и FreeBSD.
/// </summary>
public static class SequenceMath
{
    /// <summary>
    /// a предшествует b в sequence space (с учётом wrap-around).
    /// Эквивалент Linux-макроса before() из include/net/tcp.h:
    ///   static inline bool before(__u32 seq1, __u32 seq2) {
    ///       return (__s32)(seq1 - seq2) < 0;
    ///   }
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool LessThan(uint a, uint b)
        => unchecked((int)(a - b)) < 0;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool LessThanOrEqual(uint a, uint b)
        => a == b || LessThan(a, b);

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool GreaterThan(uint a, uint b)
        => LessThan(b, a);

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool GreaterThanOrEqual(uint a, uint b)
        => a == b || GreaterThan(a, b);

    /// <summary>
    /// Проверяет, попадает ли seq в полуоткрытый интервал [windowStart, windowEnd).
    /// Используется в segment acceptability test (RFC 9293, §3.4).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool InWindow(uint seq, uint windowStart, uint windowEnd)
        => LessThanOrEqual(windowStart, seq) && LessThan(seq, windowEnd);

    /// <summary>
    /// Расстояние от <paramref name="from"/> до <paramref name="to"/>
    /// в направлении увеличения sequence numbers.
    /// Distance(100, 103) = 3. Distance(0xFFFFFFFF, 2) = 3.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static uint Distance(uint from, uint to)
        => unchecked(to - from);
}
```

Обратите внимание на `AggressiveInlining` — JIT-компилятор .NET и без подсказки заинлайнит эти методы (они тривиальны), но атрибут делает намерение явным. В hot path парсинга пакетов каждый наносекундный call overhead имеет значение.

**Аналог в ядре Linux** (`include/net/tcp.h`):

```c
static inline bool before(__u32 seq1, __u32 seq2)
{
    return (__s32)(seq1-seq2) < 0;
}
#define after(seq2, seq1)   before(seq1, seq2)
```

Идентичный приём. Единственная разница — Linux использует макрос `after()` для обратного сравнения, а мы — отдельный метод `GreaterThan()`.

---

## Часть 11.3: Zero-alloc парсинг TCP-заголовка

### Зачем ref struct

В hot path обработки пакетов каждая аллокация на куче (heap allocation) — это будущая работа для GC. При rate 1 млн пакетов/с даже небольшой объект порождает ~1 млн аллокаций/с, что приведёт к частым Gen0 collections, а в худшем случае — к Gen2 stop-the-world паузам.

Решение — использовать `readonly ref struct`. Это тип, который:

1. **Живёт только на стеке** — не может быть размещён на куче, не может быть полем обычного класса, не может быть захвачен в замыкание (closure) или async state machine.
2. **Может содержать `Span<T>`** — а обычный `struct` или `class` не может, потому что `Span<T>` сам является `ref struct`.
3. **Не создаёт давления на GC** — ноль аллокаций, ноль сборок мусора.

Ограничения `ref struct` — это не недостаток, а **гарантия**: компилятор не позволит вам случайно передать `TcpSegment` в `Task.Run()` или сохранить его в `List<T>`. Если вам нужно сохранить данные сегмента надолго — скопируйте нужные поля в обычный `record struct`.

### TcpFlags

Начнём с перечисления TCP-флагов. В заголовке TCP флаги занимают биты 0--5 байта 13 (смещение 13 от начала заголовка):

```
  Бит 0: FIN
  Бит 1: SYN
  Бит 2: RST
  Бит 3: PSH
  Бит 4: ACK
  Бит 5: URG
```

```csharp
// src/TcpSlidingWindow/Core/TcpFlags.cs

namespace TcpSlidingWindow.Core;

/// <summary>
/// TCP-флаги, соответствующие битам 0–5 байта 13 TCP-заголовка.
/// Значения совпадают с wire format — можно применять как маску напрямую.
/// </summary>
[Flags]
public enum TcpFlags : byte
{
    None = 0,
    FIN  = 0x01,
    SYN  = 0x02,
    RST  = 0x04,
    PSH  = 0x08,
    ACK  = 0x10,
    URG  = 0x20,

    // Комбинации, часто встречающиеся в коде
    SynAck = SYN | ACK,
    FinAck = FIN | ACK,
    RstAck = RST | ACK,
}
```

Атрибут `[Flags]` позволяет комбинировать значения через `|` и корректно выводить их через `ToString()`: `TcpFlags.SynAck.ToString()` вернёт `"SYN, ACK"`.

### TcpSegment — readonly ref struct

Структура `TcpSegment` — это **view** поверх буфера с сырыми байтами TCP-сегмента. Она не копирует данные, а предоставляет типизированный доступ к полям заголовка через `BinaryPrimitives`.

```csharp
// src/TcpSlidingWindow/Core/TcpSegment.cs

using System.Buffers.Binary;

namespace TcpSlidingWindow.Core;

/// <summary>
/// Readonly view поверх буфера с TCP-сегментом.
/// 
/// Формат TCP-заголовка (RFC 9293, §3.1):
/// 
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |          Source Port          |       Destination Port        |  0–3
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                        Sequence Number                        |  4–7
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                    Acknowledgment Number                      |  8–11
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |  Data |     |N|C|E|U|A|P|R|S|F|                               |
/// | Offset| Rsv |S|W|C|R|C|S|S|Y|F|            Window             | 12–15
/// |       |     | |R|E|G|K|H|T|N| |                               |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |           Checksum            |         Urgent Pointer        | 16–19
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                    Options (variable)                         |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                             Data                              |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// 
/// Минимальный размер заголовка: 20 байт (Data Offset = 5).
/// Максимальный размер заголовка: 60 байт (Data Offset = 15).
/// 
/// ref struct: живёт только на стеке, может содержать Span,
/// ноль аллокаций при парсинге.
/// </summary>
public readonly ref struct TcpSegment
{
    /// <summary>Минимальный размер TCP-заголовка в байтах.</summary>
    public const int MinHeaderSize = 20;

    private readonly ReadOnlySpan<byte> _raw;

    /// <summary>
    /// Создаёт view поверх буфера. Буфер должен содержать как минимум
    /// <see cref="MinHeaderSize"/> байт; валидация — на вызывающей стороне.
    /// </summary>
    public TcpSegment(ReadOnlySpan<byte> raw) => _raw = raw;

    // ─── Поля заголовка ──────────────────────────────────────────────

    /// <summary>Source port (байты 0–1, big-endian).</summary>
    public ushort SourcePort
        => BinaryPrimitives.ReadUInt16BigEndian(_raw);

    /// <summary>Destination port (байты 2–3, big-endian).</summary>
    public ushort DestinationPort
        => BinaryPrimitives.ReadUInt16BigEndian(_raw[2..]);

    /// <summary>Sequence number (байты 4–7, big-endian).</summary>
    public uint SequenceNumber
        => BinaryPrimitives.ReadUInt32BigEndian(_raw[4..]);

    /// <summary>Acknowledgment number (байты 8–11, big-endian).</summary>
    public uint AcknowledgmentNumber
        => BinaryPrimitives.ReadUInt32BigEndian(_raw[8..]);

    /// <summary>
    /// Data Offset — размер заголовка в 32-битных словах (старшие 4 бита байта 12).
    /// Умножаем на 4, чтобы получить размер в байтах.
    /// </summary>
    public int HeaderLength => (_raw[12] >> 4) * 4;

    /// <summary>
    /// TCP-флаги (младшие 6 бит байта 13).
    /// Биты NS/CWR/ECE (байт 12, биты 0–2 и байт 13, биты 6–7)
    /// игнорируются для простоты — мы не реализуем ECN.
    /// </summary>
    public TcpFlags Flags => (TcpFlags)(_raw[13] & 0x3F);

    /// <summary>Window size (байты 14–15, big-endian). Без Window Scaling.</summary>
    public ushort WindowSize
        => BinaryPrimitives.ReadUInt16BigEndian(_raw[14..]);

    /// <summary>Checksum (байты 16–17, big-endian).</summary>
    public ushort Checksum
        => BinaryPrimitives.ReadUInt16BigEndian(_raw[16..]);

    /// <summary>Urgent pointer (байты 18–19, big-endian).</summary>
    public ushort UrgentPointer
        => BinaryPrimitives.ReadUInt16BigEndian(_raw[18..]);

    // ─── Payload ─────────────────────────────────────────────────────

    /// <summary>
    /// Payload (данные после заголовка).
    /// Пустой Span, если сегмент не содержит данных (чистый ACK, SYN, etc.).
    /// </summary>
    public ReadOnlySpan<byte> Payload => _raw[HeaderLength..];

    /// <summary>Длина payload в байтах.</summary>
    public int PayloadLength => _raw.Length - HeaderLength;

    // ─── Вспомогательные свойства ────────────────────────────────────

    public bool HasFlag(TcpFlags flag) => (Flags & flag) == flag;
    public bool IsSyn => HasFlag(TcpFlags.SYN);
    public bool IsAck => HasFlag(TcpFlags.ACK);
    public bool IsFin => HasFlag(TcpFlags.FIN);
    public bool IsRst => HasFlag(TcpFlags.RST);
    public bool IsSynAck => HasFlag(TcpFlags.SynAck);

    /// <summary>
    /// «Sequence number после последнего байта payload».
    /// Для ACK без данных: SequenceNumber.
    /// SYN и FIN «занимают» 1 sequence number каждый.
    /// </summary>
    public uint SequenceEnd
    {
        get
        {
            uint end = unchecked(SequenceNumber + (uint)PayloadLength);
            if (IsSyn) end = unchecked(end + 1);
            if (IsFin) end = unchecked(end + 1);
            return end;
        }
    }
}
```

Обратите внимание на `BinaryPrimitives.ReadUInt32BigEndian` — это метод из `System.Buffers.Binary`, который читает 4 байта в big-endian порядке. TCP/IP использует network byte order (big-endian), а x86/x64/ARM в little-endian-режиме хранят байты в обратном порядке. `BinaryPrimitives` обрабатывает это корректно и на x86 компилируется в одну инструкцию `BSWAP`.

Свойство `SequenceEnd` вычисляет номер последовательности, следующий за последним байтом сегмента. Это нужно для segment acceptability test: сегмент с `seq=100` и `len=4` занимает sequence numbers 100, 101, 102, 103 — а `SequenceEnd = 104`.

### TcpHeaderParser

Класс-фабрика для безопасного создания `TcpSegment` — проверяет минимальную длину буфера перед созданием view:

```csharp
// src/TcpSlidingWindow/Core/TcpHeaderParser.cs

namespace TcpSlidingWindow.Core;

/// <summary>
/// Безопасная фабрика для создания <see cref="TcpSegment"/>.
/// Проверяет длину буфера перед тем, как создать view.
/// </summary>
public static class TcpHeaderParser
{
    /// <summary>
    /// Пытается создать <see cref="TcpSegment"/> из буфера.
    /// Возвращает false, если буфер слишком мал для TCP-заголовка.
    /// </summary>
    /// <param name="buffer">Буфер с сырыми байтами TCP-сегмента.</param>
    /// <param name="segment">Результат — view поверх буфера.</param>
    /// <returns>true, если парсинг успешен.</returns>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out TcpSegment segment)
    {
        // Минимальная проверка: хватает ли байт на заголовок?
        if (buffer.Length < TcpSegment.MinHeaderSize)
        {
            segment = default;
            return false;
        }

        // Data Offset указывает реальный размер заголовка;
        // если он больше длины буфера — заголовок обрезан.
        int dataOffset = (buffer[12] >> 4) * 4;
        if (dataOffset < TcpSegment.MinHeaderSize || dataOffset > buffer.Length)
        {
            segment = default;
            return false;
        }

        segment = new TcpSegment(buffer);
        return true;
    }
}
```

Зачем `TryParse`, а не просто конструктор с исключением? Потому что в packet processing path исключения — это катастрофа по производительности. Некорректный пакет — не «исключительная ситуация», а обычный случай: битые пакеты, фрагменты, мусор от сканеров — всё это приходит постоянно. `TryParse` + `bool` — стандартный паттерн .NET для «может не получиться, и это нормально».

### SyntheticSegmentWriter

Для тестирования нам нужно уметь **создавать** TCP-сегменты, а не только читать их. `SyntheticSegmentWriter` записывает минимальный TCP-заголовок + payload в предоставленный буфер:

```csharp
// src/TcpSlidingWindow/Core/SyntheticSegmentWriter.cs

using System.Buffers.Binary;

namespace TcpSlidingWindow.Core;

/// <summary>
/// Формирует синтетический TCP-сегмент в предоставленном буфере.
/// Используется для тестирования — не вычисляет checksum, не устанавливает порты.
/// 
/// Все поля записываются в network byte order (big-endian)
/// через BinaryPrimitives для корректной работы на любой платформе.
/// </summary>
public static class SyntheticSegmentWriter
{
    /// <summary>Размер минимального TCP-заголовка (без опций).</summary>
    private const int HeaderSize = 20;

    /// <summary>
    /// Записывает TCP-сегмент в буфер. Возвращает количество записанных байт
    /// (заголовок + payload). Буфер должен быть достаточного размера.
    /// </summary>
    /// <param name="buffer">Целевой буфер (минимум HeaderSize + payload.Length).</param>
    /// <param name="seq">Sequence number.</param>
    /// <param name="ack">Acknowledgment number.</param>
    /// <param name="flags">TCP-флаги.</param>
    /// <param name="windowSize">Размер окна для Window-поля заголовка.</param>
    /// <param name="payload">Payload (данные). Может быть пустым.</param>
    /// <returns>Количество байт, записанных в буфер.</returns>
    public static int Write(
        Span<byte> buffer,
        uint seq,
        uint ack,
        TcpFlags flags,
        ushort windowSize,
        ReadOnlySpan<byte> payload = default)
    {
        int totalLength = HeaderSize + payload.Length;

        if (buffer.Length < totalLength)
            throw new ArgumentException(
                $"Buffer too small: need {totalLength}, have {buffer.Length}");

        // Очищаем область заголовка
        buffer[..HeaderSize].Clear();

        // Source port / Destination port — оставляем нулевыми (для тестов не важны)

        // Sequence number (байты 4–7)
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], seq);

        // Acknowledgment number (байты 8–11)
        BinaryPrimitives.WriteUInt32BigEndian(buffer[8..], ack);

        // Data Offset = 5 (20 байт / 4 = 5), записывается в старшие 4 бита байта 12
        buffer[12] = (byte)(5 << 4);

        // Flags (байт 13, младшие 6 бит)
        buffer[13] = (byte)flags;

        // Window size (байты 14–15)
        BinaryPrimitives.WriteUInt16BigEndian(buffer[14..], windowSize);

        // Checksum и Urgent Pointer — оставляем нулевыми

        // Payload
        if (!payload.IsEmpty)
            payload.CopyTo(buffer[HeaderSize..]);

        return totalLength;
    }

    /// <summary>
    /// Создаёт ACK-сегмент без payload.
    /// </summary>
    public static int WriteAck(
        Span<byte> buffer, uint seq, uint ack, ushort windowSize)
        => Write(buffer, seq, ack, TcpFlags.ACK, windowSize);

    /// <summary>
    /// Создаёт DATA+ACK-сегмент с payload.
    /// </summary>
    public static int WriteData(
        Span<byte> buffer, uint seq, uint ack, ushort windowSize,
        ReadOnlySpan<byte> payload)
        => Write(buffer, seq, ack, TcpFlags.ACK | TcpFlags.PSH, windowSize, payload);
}
```

Вместе эти четыре файла (`SequenceMath.cs`, `TcpFlags.cs`, `TcpSegment.cs`, `TcpHeaderParser.cs`, `SyntheticSegmentWriter.cs`) образуют базовый инструментарий для работы с TCP-сегментами без единой heap-аллокации. `TcpSegment` — это view поверх `Span<byte>`, `SyntheticSegmentWriter` пишет в предоставленный caller-ом буфер.

---

## Часть 11.4: SlidingWindowTracker — Version 1

### Архитектура

`SlidingWindowTracker` — это **наивная** первая версия, которая объединяет send и receive стороны в одном объекте. Мы намеренно начинаем с такого дизайна, чтобы потом (Часть 11.6, Модуль 13) увидеть, почему его нужно разделить.

Класс отвечает за:

1. **Send-сторону**: трекинг SND.UNA, SND.NXT, SND.WND, вычисление BytesInFlight и UsableWindow.
2. **Receive-сторону**: трекинг RCV.NXT, RCV.WND, segment acceptability test.
3. **Моментальный снимок (snapshot)**: `WindowSnapshot` — копия всех переменных для отладочного вывода.

### Результат приёма сегмента

Прежде чем смотреть на трекер, определим перечисление возможных результатов приёма:

```csharp
// src/TcpSlidingWindow/Core/SegmentAcceptResult.cs

namespace TcpSlidingWindow.Core;

/// <summary>
/// Результат segment acceptability test (RFC 9293, §3.4).
/// </summary>
public enum SegmentAcceptResult
{
    /// <summary>
    /// Сегмент принят: seq == RCV.NXT, данные попадают в окно.
    /// </summary>
    InOrder,

    /// <summary>
    /// Сегмент уже получен ранее: seq предшествует RCV.NXT.
    /// Скорее всего, ретрансмит — отправляем ACK, но данные не обрабатываем.
    /// </summary>
    Duplicate,

    /// <summary>
    /// Сегмент попадает в окно, но не следующий ожидаемый:
    /// seq > RCV.NXT. Нужен reassembly buffer (Модуль 12).
    /// </summary>
    OutOfOrder,

    /// <summary>
    /// Сегмент за пределами окна — отбрасываем.
    /// Либо seq слишком далеко впереди, либо данные не помещаются в окно.
    /// </summary>
    OutsideWindow,
}
```

### WindowSnapshot

Снимок состояния окна для отладки и логирования:

```csharp
// src/TcpSlidingWindow/Core/WindowSnapshot.cs

namespace TcpSlidingWindow.Core;

/// <summary>
/// Моментальный снимок состояния скользящего окна.
/// record struct: value type, по-значению копируется, автоматический ToString().
/// </summary>
public readonly record struct WindowSnapshot
{
    // ─── Send-сторона ────────────────────────────────
    public required uint SndUna  { get; init; }
    public required uint SndNxt  { get; init; }
    public required uint SndWnd  { get; init; }

    // ─── Receive-сторона ─────────────────────────────
    public required uint RcvNxt  { get; init; }
    public required uint RcvWnd  { get; init; }

    // ─── Вычисляемые метрики ─────────────────────────

    /// <summary>
    /// Байты «в полёте» — отправлены, но ещё не подтверждены.
    /// BytesInFlight = SND.NXT - SND.UNA (с учётом wrap-around).
    /// </summary>
    public uint BytesInFlight => SequenceMath.Distance(SndUna, SndNxt);

    /// <summary>
    /// Доступное окно для отправки новых данных.
    /// UsableWindow = SND.WND - BytesInFlight.
    /// Может быть 0, если окно исчерпано (window full).
    /// </summary>
    public uint UsableWindow
    {
        get
        {
            uint inFlight = BytesInFlight;
            return SndWnd > inFlight ? SndWnd - inFlight : 0;
        }
    }
}
```

`record struct` в C# 10+ — это value type с автогенерацией `Equals`, `GetHashCode`, `ToString`. Ключевое слово `required` (C# 11) заставляет вызывающий код инициализировать все свойства при создании — защита от забытых полей.

### Полный код SlidingWindowTracker

```csharp
// src/TcpSlidingWindow/Core/SlidingWindowTracker.cs

namespace TcpSlidingWindow.Core;

/// <summary>
/// Скользящее окно TCP — Version 1 (наивная объединённая реализация).
/// 
/// Хранит SND.UNA/NXT/WND (отправитель) и RCV.NXT/WND (получатель)
/// в одном объекте. Реализует segment acceptability test из RFC 9293, §3.4.
/// 
/// ВАЖНО: эта версия объединяет send и receive стороны в одном классе.
/// В Модуле 13 мы разделим их на TcpSendWindow и TcpReceiveEndpoint,
/// потому что RCV.NXT в реальном стеке вычисляется из состояния reassembly buffer,
/// а не хранится как отдельная переменная (подробнее — Часть 11.6).
/// </summary>
public sealed class SlidingWindowTracker
{
    // ─── Send-сторона ────────────────────────────────────────────────

    /// <summary>Oldest unacknowledged sequence number.</summary>
    public uint SndUna { get; private set; }

    /// <summary>Next sequence number to be sent.</summary>
    public uint SndNxt { get; private set; }

    /// <summary>
    /// Send window — сколько байт peer готов принять.
    /// Обновляется из поля Window каждого входящего ACK.
    /// </summary>
    public uint SndWnd { get; private set; }

    // ─── Receive-сторона ─────────────────────────────────────────────

    /// <summary>Next expected sequence number (следующий ожидаемый байт).</summary>
    public uint RcvNxt { get; private set; }

    /// <summary>
    /// Receive window — сколько байт мы готовы принять.
    /// В этой версии фиксировано при инициализации;
    /// в Модуле 12 будет динамически вычисляться из свободного места в reassembly buffer.
    /// </summary>
    public uint RcvWnd { get; private set; }

    // ─── Конструктор ─────────────────────────────────────────────────

    /// <summary>
    /// Инициализирует трекер после завершения three-way handshake.
    /// 
    /// <paramref name="iss"/> — наш ISN (Initial Send Sequence number).
    /// После SYN+ACK: SND.UNA = ISS+1, SND.NXT = ISS+1 (ещё ничего не отправлено).
    /// 
    /// <paramref name="irs"/> — ISN peer-а.
    /// После получения SYN: RCV.NXT = IRS+1 (SYN «занимает» 1 seq number).
    /// 
    /// <paramref name="peerWindow"/> — значение Window из SYN-ACK peer-а (SND.WND).
    /// <paramref name="ourWindow"/> — наш receive window (RCV.WND).
    /// </summary>
    public SlidingWindowTracker(uint iss, uint irs, uint peerWindow, uint ourWindow)
    {
        SndUna = unchecked(iss + 1);
        SndNxt = unchecked(iss + 1);
        SndWnd = peerWindow;

        RcvNxt = unchecked(irs + 1);
        RcvWnd = ourWindow;
    }

    // ─── Send-сторона: отправка данных ───────────────────────────────

    /// <summary>
    /// Вызывается после отправки сегмента с <paramref name="payloadLength"/> байтами.
    /// Продвигает SND.NXT вперёд на длину payload.
    /// 
    /// Не проверяет, есть ли место в окне — это ответственность вызывающего кода
    /// (проверка UsableWindow перед отправкой). В реальном стеке эту проверку
    /// выполняет tcp_may_send_now().
    /// </summary>
    /// <param name="payloadLength">Количество отправленных байт данных.</param>
    /// <returns>Sequence number отправленного сегмента (для трассировки).</returns>
    public uint OnSegmentSent(uint payloadLength)
    {
        uint seq = SndNxt;
        SndNxt = unchecked(SndNxt + payloadLength);
        return seq;
    }

    // ─── Send-сторона: обработка ACK ─────────────────────────────────

    /// <summary>
    /// Обрабатывает входящий ACK. Продвигает SND.UNA, обновляет SND.WND.
    /// 
    /// ACK number в TCP означает «я получил все байты до этого номера».
    /// Если ackNumber > SND.UNA — peer подтвердил новые данные.
    /// Если ackNumber <= SND.UNA — это duplicate ACK (уже обработан).
    /// Если ackNumber > SND.NXT — это ACK на данные, которые мы не отправляли (ошибка).
    /// </summary>
    /// <param name="ackNumber">Acknowledgment number из входящего сегмента.</param>
    /// <param name="peerWindow">Window size из входящего сегмента.</param>
    /// <returns>true, если ACK продвинул SND.UNA (подтвердил новые данные).</returns>
    public bool OnAckReceived(uint ackNumber, uint peerWindow)
    {
        // ACK за пределами отправленных данных — игнорируем
        if (SequenceMath.GreaterThan(ackNumber, SndNxt))
            return false;

        // Duplicate ACK — не продвигает окно
        if (SequenceMath.LessThanOrEqual(ackNumber, SndUna))
        {
            // Обновляем окно даже для duplicate ACK
            // (peer мог изменить window size)
            SndWnd = peerWindow;
            return false;
        }

        // Новый ACK — продвигаем SND.UNA
        SndUna = ackNumber;
        SndWnd = peerWindow;
        return true;
    }

    // ─── Receive-сторона: приём данных ───────────────────────────────

    /// <summary>
    /// Segment acceptability test из RFC 9293, §3.4.
    /// 
    /// Определяет, что делать с входящим сегментом на основе его
    /// sequence number и длины payload.
    /// 
    /// RFC 9293 определяет четыре случая:
    /// 
    ///   Segment Length  |  Receive Window  |  Test
    ///   ─────────────────────────────────────────────
    ///        0          |       0          |  SEG.SEQ == RCV.NXT
    ///        0          |      >0          |  RCV.NXT <= SEG.SEQ < RCV.NXT+RCV.WND
    ///       >0          |       0          |  not acceptable
    ///       >0          |      >0          |  RCV.NXT <= SEG.SEQ < RCV.NXT+RCV.WND
    ///                   |                  |  OR RCV.NXT <= SEG.SEQ+SEG.LEN-1 < RCV.NXT+RCV.WND
    /// 
    /// Мы упрощаем: для данных с payload > 0 проверяем, что хотя бы начало
    /// или конец сегмента попадает в окно.
    /// </summary>
    /// <param name="segSeq">Sequence number входящего сегмента.</param>
    /// <param name="segLen">Длина payload входящего сегмента (в байтах).</param>
    /// <returns>Классификация сегмента.</returns>
    public SegmentAcceptResult OnSegmentReceived(uint segSeq, uint segLen)
    {
        uint windowEnd = unchecked(RcvNxt + RcvWnd);

        // ─── Случай 1: сегмент без данных (чистый ACK) ──────────────
        if (segLen == 0)
        {
            if (RcvWnd == 0)
            {
                // Нулевое окно: принимаем только точное совпадение
                return segSeq == RcvNxt
                    ? SegmentAcceptResult.InOrder
                    : SegmentAcceptResult.OutsideWindow;
            }

            // Ненулевое окно: seq должен попасть в [RCV.NXT, RCV.NXT + RCV.WND)
            if (!SequenceMath.InWindow(segSeq, RcvNxt, windowEnd))
                return SegmentAcceptResult.OutsideWindow;

            return segSeq == RcvNxt
                ? SegmentAcceptResult.InOrder
                : SegmentAcceptResult.OutOfOrder;
        }

        // ─── Случай 2: сегмент с данными ────────────────────────────
        if (RcvWnd == 0)
        {
            // Нулевое окно — не принимаем данные
            return SegmentAcceptResult.OutsideWindow;
        }

        // Проверяем: seq предшествует RCV.NXT? → дубликат (или частичный)
        if (SequenceMath.LessThan(segSeq, RcvNxt))
        {
            // Конец сегмента за RCV.NXT? → частично новые данные
            // Для простоты v1 считаем это дубликатом
            // (в Module 12 reassembly buffer обработает частичное перекрытие)
            return SegmentAcceptResult.Duplicate;
        }

        // Начало или конец сегмента попадает в окно?
        uint segEnd = unchecked(segSeq + segLen - 1);

        bool startInWindow = SequenceMath.InWindow(segSeq, RcvNxt, windowEnd);
        bool endInWindow   = SequenceMath.InWindow(segEnd, RcvNxt, windowEnd);

        if (!startInWindow && !endInWindow)
            return SegmentAcceptResult.OutsideWindow;

        // Сегмент в окне. In-order или out-of-order?
        if (segSeq == RcvNxt)
        {
            // Точно следующий ожидаемый — продвигаем RCV.NXT
            RcvNxt = unchecked(RcvNxt + segLen);
            return SegmentAcceptResult.InOrder;
        }

        // В окне, но не следующий → out-of-order (нужен reassembly buffer)
        return SegmentAcceptResult.OutOfOrder;
    }

    // ─── Snapshot ────────────────────────────────────────────────────

    /// <summary>
    /// Создаёт моментальный снимок текущего состояния окна.
    /// Snapshot — value type, безопасно передавать куда угодно.
    /// </summary>
    public WindowSnapshot GetSnapshot() => new()
    {
        SndUna = SndUna,
        SndNxt = SndNxt,
        SndWnd = SndWnd,
        RcvNxt = RcvNxt,
        RcvWnd = RcvWnd,
    };

    // ─── Метрики (делегируют в snapshot) ─────────────────────────────

    /// <summary>
    /// Байты в полёте: SND.NXT - SND.UNA.
    /// Это данные, которые мы отправили, но ещё не получили подтверждения.
    /// </summary>
    public uint BytesInFlight => SequenceMath.Distance(SndUna, SndNxt);

    /// <summary>
    /// Доступное окно: SND.WND - BytesInFlight.
    /// Столько байт мы можем отправить прямо сейчас, не нарушая flow control.
    /// </summary>
    public uint UsableWindow
    {
        get
        {
            uint inFlight = BytesInFlight;
            return SndWnd > inFlight ? SndWnd - inFlight : 0;
        }
    }

    /// <summary>
    /// Форматированное состояние для отладочного вывода.
    /// </summary>
    public override string ToString()
        => $"SND(UNA={SndUna} NXT={SndNxt} WND={SndWnd}) " +
           $"RCV(NXT={RcvNxt} WND={RcvWnd}) " +
           $"InFlight={BytesInFlight} Usable={UsableWindow}";
}
```

Разберём ключевые решения подробнее.

**BytesInFlight и UsableWindow.** `BytesInFlight = SND.NXT - SND.UNA` — это количество байт, отправленных, но ещё не подтверждённых. Когда ACK приходит, SND.UNA продвигается, и BytesInFlight уменьшается. `UsableWindow = SND.WND - BytesInFlight` — это сколько ещё байт мы можем отправить. Если UsableWindow = 0, мы должны ждать ACK (окно закрыто).

**OnSegmentSent.** Просто продвигает SND.NXT. Не проверяет UsableWindow — это ответственность вызывающего кода. В реальном стеке Linux эту проверку выполняет `tcp_snd_wnd_test()` вызываемый из `tcp_may_send_now()`. Мы не дублируем эту логику в трекере, потому что решение «отправлять или нет» зависит от многих факторов (congestion window, Nagle, cork), которые нам пока недоступны.

**OnAckReceived.** Три случая:

1. `ackNumber > SND.NXT` — ACK на данные, которые мы не отправляли. Это ошибка протокола; в реальном стеке такой ACK вызывает отправку RST или просто игнорируется. Мы возвращаем `false`.
2. `ackNumber <= SND.UNA` — duplicate ACK. Данные уже подтверждены, но мы всё равно обновляем SND.WND (peer мог отправить window update).
3. `ackNumber > SND.UNA && ackNumber <= SND.NXT` — нормальный ACK. Продвигаем SND.UNA.

**OnSegmentReceived.** Segment acceptability test — самая сложная часть. RFC 9293 (S3.4, таблица 5) задаёт четыре строки в зависимости от длины сегмента и размера окна. Наша реализация обрабатывает все случаи и возвращает один из четырёх результатов: `InOrder`, `Duplicate`, `OutOfOrder`, `OutsideWindow`.

Обратите внимание на критически важную деталь: когда сегмент `InOrder`, мы **продвигаем RCV.NXT**. Это означает, что `SlidingWindowTracker` сам модифицирует состояние при приёме in-order данных. В Модуле 12 мы увидим, почему это проблематично — когда появится reassembly buffer, RCV.NXT должен определяться состоянием буфера, а не устанавливаться трекером напрямую.

---

## Часть 11.5: Пример прогона

### Сценарий

Представим следующую ситуацию:

- **MSS = 4 байта** (искусственно маленький для наглядности).
- **Peer window = 12 байт** (вмещает 3 сегмента по 4 байта).
- **Payload = "ABCDEFGHIJKLMNOPQRST"** — 20 байт, 5 сегментов.
- **ISS (наш) = 1000**, **IRS (peer) = 5000**.
- Сегменты 3 и 4 **намеренно переставлены** на wire — чтобы продемонстрировать OutOfOrder.

Ожидаемый порядок отправки (sender side):

```
Сегмент 1: seq=1001  payload="ABCD"  (байты 1001–1004)
Сегмент 2: seq=1005  payload="EFGH"  (байты 1005–1008)
Сегмент 3: seq=1009  payload="IJKL"  (байты 1009–1012)
  -- window full: BytesInFlight=12, UsableWindow=0 → ждём ACK --
Сегмент 4: seq=1013  payload="MNOP"  (байты 1013–1016)
Сегмент 5: seq=1017  payload="QRST"  (байты 1017–1020)
```

Ожидаемый порядок доставки (receiver side) — сегменты 3 и 4 переставлены:

```
Доставлен сегмент 1: seq=1001 → InOrder, RCV.NXT=1005
Доставлен сегмент 2: seq=1005 → InOrder, RCV.NXT=1009
Доставлен сегмент 4: seq=1013 → OutOfOrder (пропущен 1009)
Доставлен сегмент 3: seq=1009 → InOrder, RCV.NXT=1013
Доставлен сегмент 5: seq=1017 → OutOfOrder (пропущен 1013-1016 в v1)
```

### Полный код Program.cs

```csharp
// src/TcpSlidingWindow/Program.cs

using System.Text;
using TcpSlidingWindow.Core;

// ═══════════════════════════════════════════════════════════════════
//  Sliding Window Demo — Модуль 11
//
//  Демонстрация:
//   1. Отправка 5 сегментов по 4 байта (MSS=4)
//   2. Окно peer-а = 12 байт (3 сегмента)
//   3. Окно закрывается после 3-го сегмента
//   4. ACK на первый сегмент открывает окно
//   5. Сегменты 3 и 4 переставлены на стороне получателя
// ═══════════════════════════════════════════════════════════════════

const uint ISS = 1000;                // Наш Initial Sequence Number
const uint IRS = 5000;                // ISN peer-а
const uint PeerWindow = 12;           // Peer готов принять 12 байт
const uint OurWindow = 65535;         // Наше окно (не ограничиваем)
const int MSS = 4;                    // Maximum Segment Size

// Payload: 20 байт → 5 сегментов по 4 байта
byte[] fullPayload = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRST");
int totalSegments = fullPayload.Length / MSS;

Console.WriteLine("╔══════════════════════════════════════════════════════╗");
Console.WriteLine("║     TCP Sliding Window Demo — Module 11             ║");
Console.WriteLine("╠══════════════════════════════════════════════════════╣");
Console.WriteLine($"║  ISS={ISS}  IRS={IRS}  PeerWindow={PeerWindow}  MSS={MSS,-5}  ║");
Console.WriteLine($"║  Payload: \"{Encoding.ASCII.GetString(fullPayload)}\" ({fullPayload.Length} bytes)  ║");
Console.WriteLine($"║  Segments: {totalSegments}                                     ║");
Console.WriteLine("╚══════════════════════════════════════════════════════╝");
Console.WriteLine();

// ─── Инициализация трекеров ──────────────────────────────────────
// Sender и Receiver — два независимых трекера, как две стороны соединения.

var sender   = new SlidingWindowTracker(ISS, IRS, PeerWindow, OurWindow);
var receiver = new SlidingWindowTracker(IRS, ISS, OurWindow, PeerWindow);

// Буфер для формирования сегментов (reuse — zero alloc в цикле)
byte[] segmentBuffer = new byte[TcpSegment.MinHeaderSize + MSS];

int step = 0;

void PrintStep(string action, string detail)
{
    step++;
    Console.WriteLine($"  [step {step,2}] {action,-12} {detail}");
    Console.WriteLine($"           sender:   {sender}");
    Console.WriteLine($"           receiver: {receiver}");
    Console.WriteLine();
}

// ═══════════════════════════════════════════════════════════════════
//  ФАЗА 1: Отправка сегментов (sender side)
// ═══════════════════════════════════════════════════════════════════

Console.WriteLine("── ФАЗА 1: Отправка ───────────────────────────────────");
Console.WriteLine();

// Список отправленных сегментов (seq → payload) для последующей "доставки"
var sentSegments = new List<(uint Seq, byte[] Payload)>();

for (int i = 0; i < totalSegments; i++)
{
    uint usable = sender.UsableWindow;

    if (usable < (uint)MSS)
    {
        Console.WriteLine($"  *** WINDOW FULL: UsableWindow={usable}, BytesInFlight={sender.BytesInFlight} ***");
        Console.WriteLine($"  *** Нужен ACK, чтобы продолжить отправку ***");
        Console.WriteLine();

        // Симулируем ACK от receiver-а на первый сегмент
        // ACK number = RCV.NXT receiver-а после приёма первого сегмента
        // В реальной жизни ACK приходит по сети; здесь мы его генерируем.
        uint ackNum = unchecked(ISS + 1 + (uint)MSS);  // ACK на первые 4 байта
        ushort newWindow = (ushort)PeerWindow;

        int ackLen = SyntheticSegmentWriter.WriteAck(
            segmentBuffer, IRS + 1, ackNum, newWindow);

        if (TcpHeaderParser.TryParse(segmentBuffer.AsSpan(0, ackLen), out var ackSeg))
        {
            bool advanced = sender.OnAckReceived(ackSeg.AcknowledgmentNumber, ackSeg.WindowSize);
            PrintStep("RECV ACK",
                $"ack={ackSeg.AcknowledgmentNumber} win={ackSeg.WindowSize}" +
                $" advanced={advanced}");
        }
    }

    // Формируем и «отправляем» сегмент
    byte[] payload = fullPayload[(i * MSS)..((i + 1) * MSS)];
    uint seq = sender.OnSegmentSent((uint)MSS);
    sentSegments.Add((seq, payload));

    string payloadStr = Encoding.ASCII.GetString(payload);
    PrintStep("SEND",
        $"seq={seq}  len={MSS} payload=\"{payloadStr}\"");
}

// ═══════════════════════════════════════════════════════════════════
//  ФАЗА 2: Доставка сегментов receiver-у (с переупорядочиванием)
// ═══════════════════════════════════════════════════════════════════

Console.WriteLine("── ФАЗА 2: Доставка (receiver side) ───────────────────");
Console.WriteLine();

// Порядок доставки: 0, 1, 3, 2, 4 (сегменты 3 и 4 переставлены)
int[] deliveryOrder = [0, 1, 3, 2, 4];

foreach (int idx in deliveryOrder)
{
    var (seq, payload) = sentSegments[idx];
    string payloadStr = Encoding.ASCII.GetString(payload);

    SegmentAcceptResult result = receiver.OnSegmentReceived(seq, (uint)payload.Length);

    PrintStep("DELIVER",
        $"seq={seq}  len={payload.Length} payload=\"{payloadStr}\" -> {result}");

    // Если InOrder — генерируем ACK sender-у
    if (result == SegmentAcceptResult.InOrder)
    {
        uint ackNum = receiver.RcvNxt;

        int ackLen = SyntheticSegmentWriter.WriteAck(
            segmentBuffer, IRS + 1, ackNum, (ushort)OurWindow);

        if (TcpHeaderParser.TryParse(segmentBuffer.AsSpan(0, ackLen), out var ackSeg))
        {
            bool advanced = sender.OnAckReceived(
                ackSeg.AcknowledgmentNumber, ackSeg.WindowSize);
            PrintStep("ACK->SND",
                $"ack={ackSeg.AcknowledgmentNumber} -> sender advanced={advanced}");
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
//  Финальное состояние
// ═══════════════════════════════════════════════════════════════════

Console.WriteLine("── ИТОГ ───────────────────────────────────────────────");
Console.WriteLine();
Console.WriteLine($"  Sender:   {sender}");
Console.WriteLine($"  Receiver: {receiver}");
Console.WriteLine();

var sndSnap = sender.GetSnapshot();
var rcvSnap = receiver.GetSnapshot();

Console.WriteLine($"  Sender BytesInFlight:  {sndSnap.BytesInFlight}");
Console.WriteLine($"  Sender UsableWindow:   {sndSnap.UsableWindow}");
Console.WriteLine($"  Receiver RCV.NXT:      {rcvSnap.RcvNxt}");
Console.WriteLine();
Console.WriteLine("Done.");
```

### Трассировка выполнения

Запустив `dotnet run --project src/TcpSlidingWindow`, вы увидите примерно следующее:

```
╔══════════════════════════════════════════════════════╗
║     TCP Sliding Window Demo — Module 11             ║
╠══════════════════════════════════════════════════════╣
║  ISS=1000  IRS=5000  PeerWindow=12  MSS=4           ║
║  Payload: "ABCDEFGHIJKLMNOPQRST" (20 bytes)         ║
║  Segments: 5                                        ║
╚══════════════════════════════════════════════════════╝

── ФАЗА 1: Отправка ───────────────────────────────────

  [step  1] SEND         seq=1001  len=4 payload="ABCD"
           sender:   SND(UNA=1001 NXT=1005 WND=12) RCV(NXT=5001 WND=65535) InFlight=4 Usable=8
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1001 WND=12) InFlight=0 Usable=65535

  [step  2] SEND         seq=1005  len=4 payload="EFGH"
           sender:   SND(UNA=1001 NXT=1009 WND=12) RCV(NXT=5001 WND=65535) InFlight=8 Usable=4
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1001 WND=12) InFlight=0 Usable=65535

  [step  3] SEND         seq=1009  len=4 payload="IJKL"
           sender:   SND(UNA=1001 NXT=1013 WND=12) RCV(NXT=5001 WND=65535) InFlight=12 Usable=0
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1001 WND=12) InFlight=0 Usable=65535

  *** WINDOW FULL: UsableWindow=0, BytesInFlight=12 ***
  *** Нужен ACK, чтобы продолжить отправку ***

  [step  4] RECV ACK     ack=1005 win=12 advanced=true
           sender:   SND(UNA=1005 NXT=1013 WND=12) RCV(NXT=5001 WND=65535) InFlight=8 Usable=4
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1001 WND=12) InFlight=0 Usable=65535

  [step  5] SEND         seq=1013  len=4 payload="MNOP"
           sender:   SND(UNA=1005 NXT=1017 WND=12) RCV(NXT=5001 WND=65535) InFlight=12 Usable=0
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1001 WND=12) InFlight=0 Usable=65535

  *** WINDOW FULL: UsableWindow=0, BytesInFlight=12 ***
  *** Нужен ACK, чтобы продолжить отправку ***

  [step  6] RECV ACK     ack=1009 win=12 advanced=true
           sender:   SND(UNA=1009 NXT=1017 WND=12) RCV(NXT=5001 WND=65535) InFlight=8 Usable=4
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1001 WND=12) InFlight=0 Usable=65535

  [step  7] SEND         seq=1017  len=4 payload="QRST"
           sender:   SND(UNA=1009 NXT=1021 WND=12) RCV(NXT=5001 WND=65535) InFlight=12 Usable=0
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1001 WND=12) InFlight=0 Usable=65535

── ФАЗА 2: Доставка (receiver side) ───────────────────

  [step  8] DELIVER      seq=1001  len=4 payload="ABCD" -> InOrder
           sender:   SND(UNA=1009 NXT=1021 WND=12) RCV(NXT=5001 WND=65535) InFlight=12 Usable=0
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1005 WND=12) InFlight=0 Usable=65535

  [step  9] ACK->SND     ack=1005 -> sender advanced=false
           sender:   SND(UNA=1009 NXT=1021 WND=65535) RCV(NXT=5001 WND=65535) InFlight=12 Usable=65523
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1005 WND=12) InFlight=0 Usable=65535

  [step 10] DELIVER      seq=1005  len=4 payload="EFGH" -> InOrder
           sender:   SND(UNA=1009 NXT=1021 WND=65535) RCV(NXT=5001 WND=65535) InFlight=12 Usable=65523
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1009 WND=12) InFlight=0 Usable=65535

  [step 11] ACK->SND     ack=1009 -> sender advanced=false
           sender:   SND(UNA=1009 NXT=1021 WND=65535) RCV(NXT=5001 WND=65535) InFlight=12 Usable=65523
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1009 WND=12) InFlight=0 Usable=65535

  [step 12] DELIVER      seq=1013  len=4 payload="MNOP" -> OutOfOrder
           sender:   SND(UNA=1009 NXT=1021 WND=65535) RCV(NXT=5001 WND=65535) InFlight=12 Usable=65523
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1009 WND=12) InFlight=0 Usable=65535

  [step 13] DELIVER      seq=1009  len=4 payload="IJKL" -> InOrder
           sender:   SND(UNA=1009 NXT=1021 WND=65535) RCV(NXT=5001 WND=65535) InFlight=12 Usable=65523
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1013 WND=12) InFlight=0 Usable=65535

  [step 14] ACK->SND     ack=1013 -> sender advanced=true
           sender:   SND(UNA=1013 NXT=1021 WND=65535) RCV(NXT=5001 WND=65535) InFlight=8 Usable=65527
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1013 WND=12) InFlight=0 Usable=65535

  [step 15] DELIVER      seq=1017  len=4 payload="QRST" -> OutOfOrder
           sender:   SND(UNA=1013 NXT=1021 WND=65535) RCV(NXT=5001 WND=65535) InFlight=8 Usable=65527
           receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1013 WND=12) InFlight=0 Usable=65535

── ИТОГ ───────────────────────────────────────────────

  Sender:   SND(UNA=1013 NXT=1021 WND=65535) RCV(NXT=5001 WND=65535) InFlight=8 Usable=65527
  Receiver: SND(UNA=5001 NXT=5001 WND=65535) RCV(NXT=1013 WND=12) InFlight=0 Usable=65535

  Sender BytesInFlight:  8
  Sender UsableWindow:   65527
  Receiver RCV.NXT:      1013

Done.
```

### Что мы видим в трассировке

1. **Steps 1--3 (SEND):** Sender отправляет три сегмента. После каждого SND.NXT увеличивается на 4, BytesInFlight растёт: 4 -> 8 -> 12. После step 3 UsableWindow = 0 — **окно закрыто**.

2. **Step 4 (RECV ACK):** Sender получает ACK с ack=1005 (подтверждает "ABCD"). SND.UNA продвигается 1001 -> 1005, BytesInFlight падает 12 -> 8, UsableWindow = 4. **Окно открылось** — можно отправить ещё один сегмент.

3. **Step 5 (SEND):** Sender отправляет "MNOP", окно снова закрывается (BytesInFlight = 12).

4. **Step 8--9 (DELIVER + ACK):** Receiver получает seq=1001 ("ABCD") — InOrder. RCV.NXT продвигается 1001 -> 1005. ACK отправляется sender-у, но ack=1005 <= SND.UNA=1009, поэтому sender видит это как duplicate ACK (advanced=false). Однако SND.WND обновляется до 65535 — это корректно: peer мог увеличить свой advertised window.

5. **Step 12 (DELIVER seq=1013):** Receiver получает "MNOP" — но ожидал seq=1009 ("IJKL"). Результат: **OutOfOrder**. В реальном TCP этот сегмент попал бы в reassembly buffer; в нашей v1 мы просто классифицируем его и не продвигаем RCV.NXT.

6. **Step 13 (DELIVER seq=1009):** Receiver получает "IJKL" — это именно то, что ожидалось (seq == RCV.NXT). Результат: **InOrder**, RCV.NXT продвигается 1009 -> 1013.

7. **Step 15 (DELIVER seq=1017):** Receiver получает "QRST" — но ожидал seq=1013 ("MNOP", который пришёл раньше и был отмечен как OutOfOrder). Результат: **OutOfOrder**.

Ключевое наблюдение: сегмент "MNOP" (seq=1013) пришёл в step 12 как OutOfOrder, но в v1 мы его **потеряли** — у нас нет reassembly buffer, чтобы сохранить его и вставить на место, когда gap будет заполнен. Именно это мы исправим в Модуле 12.

---

## Часть 11.6: Почему этот класс потом разделили

### Проблема: два источника истины

В `SlidingWindowTracker` переменные SND и RCV живут в одном объекте. Это удобно для простой демонстрации, но создаёт **структурную проблему**, которая проявится в Модуле 12.

Рассмотрим `RCV.NXT`. В нашей v1 `OnSegmentReceived` **напрямую устанавливает** `RcvNxt`, когда сегмент in-order. Но когда в Модуле 12 мы добавим reassembly buffer (реальный `SortedList<uint, Segment>` с out-of-order данными), произойдёт следующее:

1. Reassembly buffer хранит принятые сегменты и знает, какой непрерывный диапазон байт уже собран.
2. `RCV.NXT` — это «следующий ожидаемый байт», то есть `reassembly_buffer.ContiguousEnd`.
3. Если `SlidingWindowTracker` хранит свой собственный `RcvNxt` **и** reassembly buffer тоже вычисляет `ContiguousEnd` — появляются **два источника истины** для одной и той же величины.

Два источника истины = гарантированный баг. Рано или поздно они разойдутся: вы обновите `RcvNxt` в трекере, но забудете обновить буфер, или наоборот. В нашем простом случае это может выглядеть так:

```
Трекер:      RCV.NXT = 1013 (обновлён при InOrder)
Реассемблер: ContiguousEnd = 1009 (не видел InOrder-сегмент, обработан трекером напрямую)
→ Расхождение: трекер думает, что байты до 1013 получены, буфер — что только до 1009.
```

### Решение: разделение

В Модуле 13 мы разделим `SlidingWindowTracker` на два класса:

| Класс | Ответственность |
|---|---|
| `TcpSendWindow` | SND.UNA, SND.NXT, SND.WND, BytesInFlight, UsableWindow |
| `TcpReceiveEndpoint` | Reassembly buffer + RCV.NXT (вычисляемый из буфера) + RCV.WND (= свободное место в буфере) |

`TcpReceiveEndpoint` не хранит `RcvNxt` как поле — он **вычисляет** его как `_buffer.ContiguousEnd`. Аналогично, `RcvWnd` не хранится — это `_bufferCapacity - _buffer.TotalBytes`. Единственный источник истины — reassembly buffer.

Это не стилистический рефакторинг и не погоня за «чистой архитектурой». Это исправление **конкретного бага**, который неизбежен при текущем дизайне. Мы намеренно начали с наивной версии, чтобы вы увидели проблему на практике, а не приняли разделение на веру.

Причина, по которой send и receive стороны объединены в v1 — педагогическая: проще увидеть все переменные TCP-окна в одном месте и понять, как они взаимодействуют. Но в production-коде такое объединение — антипаттерн.

---

## Часть 11.7: Production Corner

### Linux: struct tcp_sock

В ядре Linux все переменные скользящего окна хранятся в `struct tcp_sock` (`include/linux/tcp.h`). Вот ключевые поля, соответствующие нашим:

```c
// include/linux/tcp.h (упрощённо)
struct tcp_sock {
    // ─── Send-сторона ────────────────────────
    u32 snd_una;    // oldest unacked sequence number
    u32 snd_nxt;    // next sequence number to send
    u32 snd_wnd;    // send window (from last ACK)
    u32 snd_wl1;    // seq of last window update
    u32 snd_wl2;    // ack of last window update

    // ─── Receive-сторона ─────────────────────
    u32 rcv_nxt;    // next expected sequence number
    u32 rcv_wnd;    // current receive window
    u32 rcv_wup;    // rcv_nxt at last window update sent

    // ─── Congestion (Модуль 13) ──────────────
    u32 snd_cwnd;   // congestion window (segments)
    u32 snd_ssthresh; // slow-start threshold

    // ─── Reassembly (Модуль 12) ──────────────
    struct sk_buff_head out_of_order_queue;
    // ...
};
```

Обратите внимание на `snd_wl1` и `snd_wl2` — переменные для **window update validation** (RFC 9293, S3.4). Они нужны, чтобы не принять устаревший (reordered) ACK с меньшим window size. Наша v1 не реализует эту проверку — мы просто берём window из последнего ACK. В реальном стеке это может привести к ложному уменьшению окна; Linux защищается так:

```c
// net/ipv4/tcp_input.c (упрощённо)
static void tcp_ack_update_window(struct sock *sk, const struct sk_buff *skb,
                                  u32 ack, u32 ack_seq)
{
    struct tcp_sock *tp = tcp_sk(sk);

    if (/* window update is valid: SND.WL1 < SEG.SEQ ||
           (SND.WL1 == SEG.SEQ && SND.WL2 <= SEG.ACK) */)
    {
        tp->snd_wnd = ntohs(tcp_hdr(skb)->window) << tp->rx_opt.snd_wscale;
        tp->snd_wl1 = ack_seq;
        tp->snd_wl2 = ack;
    }
}
```

### Window Scaling (RFC 7323)

В нашей реализации поле Window в TCP-заголовке содержит реальный размер окна. В настоящем TCP это не так — поле Window всего 16 бит (максимум 65 535), а реальные окна на быстрых линках достигают сотен мегабайт.

RFC 7323 решает эту проблему через **Window Scale Option**, которая согласовывается при three-way handshake:

```
TCP Option: Window Scale
  Kind: 3
  Length: 3
  Shift.Cnt: 0–14
```

`Shift.Cnt` — это степень двойки, на которую нужно сдвинуть значение поля Window, чтобы получить реальный размер окна:

```
actual_window = header_window_value << shift_count
```

Пример:
```
Window (заголовок): 16384
Shift.Cnt: 7
Actual window: 16384 << 7 = 2 097 152 (2 МБ)
```

Максимальный Shift.Cnt = 14, что даёт максимальное окно:

```
65535 << 14 = 1 073 725 440 (~1 ГБ)
```

Наша реализация использует `uint` (32 бита) для хранения SND.WND и RCV.WND, что вмещает любое реальное значение окна. Но мы не парсим Window Scale Option из SYN/SYN-ACK и не применяем shift при чтении поля Window. В production-реализации это обязательно.

### tcp_may_send_now()

В нашей реализации решение «можно ли отправить» — это простое сравнение `UsableWindow >= MSS`. В реальном Linux это значительно сложнее. Функция `tcp_may_send_now()` проверяет:

```c
// net/ipv4/tcp_output.c (упрощённо)
static bool tcp_may_send_now(struct sock *sk)
{
    struct tcp_sock *tp = tcp_sk(sk);
    struct sk_buff *skb = tcp_send_head(sk);

    return skb &&
        tcp_snd_wnd_test(tp, skb, tcp_current_mss(sk)) &&  // flow control window
        tcp_cwnd_test(tp, skb) &&                           // congestion window
        !tcp_pacing_check(sk);                              // pacing (BBR, etc.)
}
```

Три уровня контроля:

1. **Flow control** (`tcp_snd_wnd_test`): наш UsableWindow — можно ли по окну peer-а?
2. **Congestion control** (`tcp_cwnd_test`): можно ли по congestion window? (Модуль 13)
3. **Pacing** (`tcp_pacing_check`): BBR/FQ задаёт rate, не пора ли подождать? (Модуль 13)

Отправка разрешена только если все три проверки пройдены. Наша v1 реализует только первую.

### Silly Window Syndrome (SWS)

Ещё один production-нюанс, который мы пока игнорируем. Если receiver постоянно объявляет маленькое окно (1-2 байта), sender будет отправлять крошечные сегменты, и overhead заголовков (40+ байт на каждый) уничтожит пропускную способность. Это называется **Silly Window Syndrome** (RFC 9293, S3.8.6.2.1).

Защита — двусторонняя:
- **Receiver**: не объявляет окно меньше MSS (или 1/2 буфера), даже если немного места уже есть. Вместо этого объявляет window=0 и ждёт, пока освободится достаточно.
- **Sender**: не отправляет сегмент меньше MSS, если есть неподтверждённые данные (алгоритм Nagle, RFC 896).

В нашем примере MSS=4 и window=12, поэтому SWS не проявляется. Но в production с MSS=1460 и автоматически настраиваемым окном — это реальная проблема.

---

## Часть 11.8: Карта RFC для этого модуля

| Механизм | RFC | Секция | Файл в проекте |
|---|---|---|---|
| Sequence number arithmetic | RFC 1982 | S3.2 (Serial Number Arithmetic) | `Core/SequenceMath.cs` |
| TCP header format | RFC 9293 | S3.1 (Header Format) | `Core/TcpSegment.cs`, `Core/TcpFlags.cs` |
| Send/Receive sequence variables | RFC 9293 | S3.3.1 (Sequence Variables) | `Core/SlidingWindowTracker.cs` |
| Segment acceptability test | RFC 9293 | S3.4 (Sequence Numbers, Table 5) | `Core/SlidingWindowTracker.cs` |
| Window Scaling | RFC 7323 | S2 (TCP Window Scale Option) | Не реализовано (v1 использует raw 16-bit) |
| Silly Window Syndrome | RFC 9293 | S3.8.6.2.1 (Sender SWS Avoidance) | Не реализовано (v1) |
| Initial Sequence Number | RFC 9293 | S3.4.1 (ISN Generation) | Используем фиксированный ISN (демо) |

### Файлы проекта

```
src/TcpSlidingWindow/
  Program.cs                           — демонстрация (Часть 11.5)
  Core/
    SequenceMath.cs                    — арифметика seq numbers (Часть 11.2)
    TcpFlags.cs                        — перечисление TCP-флагов (Часть 11.3)
    TcpSegment.cs                      — zero-alloc парсер заголовка (Часть 11.3)
    TcpHeaderParser.cs                 — фабрика TcpSegment (Часть 11.3)
    SyntheticSegmentWriter.cs          — формирование тестовых сегментов (Часть 11.3)
    SegmentAcceptResult.cs             — результат segment acceptability (Часть 11.4)
    WindowSnapshot.cs                  — снимок состояния окна (Часть 11.4)
    SlidingWindowTracker.cs            — скользящее окно v1 (Часть 11.4)
```

---

## Что дальше

В этом модуле мы построили фундамент: sequence number arithmetic, zero-alloc парсинг TCP-заголовка и наивный трекер скользящего окна. Мы увидели, как окно закрывается при исчерпании UsableWindow, как ACK открывает его обратно, и как out-of-order сегменты классифицируются, но **теряются** — у нас нет буфера для их хранения.

В **Модуле 12** мы исправим это: добавим reassembly buffer на основе `SortedList<uint, ReadOnlyMemory<byte>>`, который будет хранить out-of-order сегменты и собирать непрерывный поток байт по мере заполнения пробелов. `RCV.NXT` перестанет быть полем трекера и станет вычисляться из состояния буфера.

---

**Предыдущий модуль:** [Модуль 10: EVE-NG Lab](Module-10-EVE-NG-Lab.md) — боевой полигон для .NET и Chaos Engineering.

**Следующий модуль:** [Модуль 12: Out-of-Order Reassembly](Module-12-Out-of-Order-Reassembly.md) — буфер переупорядочивания на `SortedList<uint, Segment>`.
