# Модуль 12: Out-of-Order Reassembly

*«Почему TCP собирает байты, а не пакеты -- и как это реализовать»*

> **Исходный код:** `src/TcpOutOfOrderReassembly/`  
> **Запуск:** `dotnet run --project src/TcpOutOfOrderReassembly`  
> **Предыдущий модуль:** [Модуль 11: Sliding Window](Module-11-Sliding-Window-Implementation.md)  
> **Следующий модуль:** [Модуль 13: TCP Receive Pipeline](Module-13-TCP-Receive-Pipeline.md)

---

Этот модуль -- сердце приёмной стороны TCP. Всё, что мы строили до сих пор -- конечный автомат, скользящее окно, арифметика sequence numbers -- существует ради одного: передать приложению **непрерывный поток байт** из потока пакетов, которые приходят в произвольном порядке, с дубликатами, перекрытиями и пробелами. Именно reassembler превращает хаос IP-доставки в абстракцию «надёжный байтовый поток», которую приложение воспринимает как данность.

Мы пройдём от постановки задачи до полной реализации на C#, разбирая каждое проектное решение через вопрос «Почему именно так?».

---

## Часть 12.1 -- Почему вообще нужен Reassembler

Зачем вообще нужен специальный компонент для сборки данных? Разве TCP не гарантирует порядок?

TCP *гарантирует* порядок -- но только для приложения. Между отправителем и получателем лежит IP-сеть, которая не даёт никаких гарантий: пакеты могут прийти в любом порядке, некоторые потеряются, некоторые будут продублированы. Задача reassembler -- превратить этот хаос в упорядоченный поток.

Рассмотрим конкретный пример:

```
Получили:
SEQ=1000  "Hello "     (6 байт, диапазон [1000, 1006))
SEQ=1012  "world!"     (6 байт, диапазон [1012, 1018))
SEQ=1006  "TCP "       (4 байт, диапазон [1006, 1010))

Порядок получения:   1000 -> 1012 -> 1006
Порядок для приложения: "Hello TCP world!"
```

Что происходит шаг за шагом:

1. Приходит `SEQ=1000, "Hello "`. RCV.NXT был 1000 -- сегмент в порядке, отдаём приложению. RCV.NXT = 1006.

2. Приходит `SEQ=1012, "world!"`. RCV.NXT = 1006, а сегмент начинается с 1012 -- между 1006 и 1012 дыра в 6 байт (ещё неизвестно, что там будет). Отдать приложению нельзя: если отдать "world!" сейчас, приложение получит "Hello world!" без "TCP " посередине. Буферизуем.

3. Приходит `SEQ=1006, "TCP "`. Диапазон [1006, 1010). Начинается ровно с RCV.NXT -- отдаём приложению. RCV.NXT = 1010. Но постойте: между 1010 и 1012 ещё дыра в 2 байта. Буферизованный "world!" начинается с 1012, а не с 1010. Значит, "world!" пока остаётся в буфере.

Ключевой инсайт: TCP гарантирует **упорядоченный байтовый поток**, а не **упорядоченные пакеты**. Reassembler -- это компонент, который обеспечивает эту гарантию на приёмной стороне.

```
Байтовый поток (как видит приложение):
позиция:  1000 1001 1002 1003 1004 1005 1006 1007 1008 1009 1010 1011 1012 ...
данные:    H    e    l    l    o    _    T    C    P    _    ?    ?    w    ...
                                                        ^
                                                      RCV.NXT = 1010
                                                      (первый отсутствующий байт)
```

RCV.NXT всегда указывает на первый байт, который ещё не может быть отдан приложению. Всё, что находится левее -- уже доставлено. Всё, что правее RCV.NXT и при этом получено -- сохранено в буфере out-of-order сегментов.

---

## Часть 12.2 -- Почему нельзя просто отсортировать List<Segment>

Первая идея, которая приходит в голову: давайте просто складывать пришедшие сегменты в список, сортировать по sequence number и отдавать по порядку.

```csharp
// Наивный подход -- НЕ РАБОТАЕТ
List<Segment> buffer = new();
buffer.Add(segment);
buffer.Sort((a, b) => a.Seq.CompareTo(b.Seq));
foreach (var s in buffer)
    Deliver(s);
```

На первый взгляд разумно. Но рассмотрим ситуацию с перекрытием:

```
Сегмент A: [1000, 1050)  -- 50 байт
Сегмент B: [1020, 1080)  -- 60 байт

После сортировки:
  A: [1000, 1050)
  B: [1020, 1080)

Если просто отдать A, затем B:
  байты 1000..1019  -- из A (правильно)
  байты 1020..1049  -- из A (правильно)
  байты 1020..1049  -- из B (ДУБЛИКАТ! Приложение получит их дважды!)
  байты 1050..1079  -- из B (правильно)
```

Проблема очевидна: после сортировки перекрытие никуда не делось. Приложение получит 30 лишних байт. Для текста это просто мусор, для бинарного протокола -- катастрофа: каждый последующий message boundary сдвинется, и всё рассыплется.

Перекрытия -- не теоретическая проблема. Они возникают в реальных сетях:

- **Ретрансмиссия** -- отправитель не получил ACK, решил, что сегмент потерян, и послал его снова. Но оригинал просто задержался -- теперь получатель имеет два одинаковых (или почти одинаковых) сегмента.
- **Partial retransmission** -- отправитель может повторить не весь потерянный сегмент, а только его часть, или объединить несколько маленьких сегментов в один большой при повторной отправке.
- **Middlebox resegmentation** -- некоторые middleboxes (WAN-оптимизаторы, прокси) разбивают большие сегменты на маленькие или объединяют маленькие в большие.

Вывод: нам нужна не сортировка, а **range-aware merging** -- алгоритм, который при вставке нового диапазона обрезает его по границам уже сохранённых данных, сохраняя только действительно новые байты.

---

## Часть 12.3 -- Почему хранить именно диапазоны [Start, End)

Раз мы храним не пакеты, а байтовые диапазоны, как именно представить их в памяти?

Визуализация проблемы:

```
Позиция:  0    5    10   15   20   25   30
          |----|----|----|----|----|----|
Данные:   -----AAAAAA------
                    --------BBBBBB---
                                ----CCCC-

Сохранено:
  [5,  11)   -- данные A
  [14, 20)   -- данные B
  [22, 26)   -- данные C

При вставке нового диапазона [8, 24):
  [8,  11)  -- уже есть (часть A), пропускаем
  [11, 14)  -- НОВОЕ, сохраняем
  [14, 20)  -- уже есть (B целиком), пропускаем
  [20, 22)  -- НОВОЕ, сохраняем
  [22, 24)  -- уже есть (часть C), пропускаем
```

Мы используем полуоткрытые интервалы `[Start, End)`, где `Start` включён, а `End` -- нет. Почему именно полуоткрытые?

1. **Длина = End - Start**. Никаких `+1` или `-1`, которые порождают off-by-one ошибки.

2. **Смежные диапазоны стыкуются точно**: `[100, 200)` и `[200, 300)` покрывают `[100, 300)` без пробелов и наложений. Если бы мы использовали закрытые интервалы `[100, 199]` и `[200, 299]`, то граничные случаи стали бы минным полем.

3. **Пустой диапазон** -- это `[x, x)`, где Start == End. Не нужен отдельный случай.

4. **Проверка принадлежности** проста: `Start <= value < End`.

Это стандартная конвенция в computer science (C-массивы, итераторы STL, Range в Python, Span в C#). Наш reassembler хранит коллекцию непересекающихся полуоткрытых интервалов, каждый с ассоциированными данными.

---

## Часть 12.4 -- Почему нельзя использовать uint внутри дерева

Итак, нам нужно хранить набор диапазонов, упорядоченных по Start. Естественный выбор -- `SortedDictionary<uint, BufferedSegment>`, где ключ -- TCP sequence number (uint32). Что может пойти не так?

TCP sequence number -- это 32-битное беззнаковое целое, которое оборачивается через ноль:

```
4294967293   (0xFFFF_FFFD)
4294967294   (0xFFFF_FFFE)
4294967295   (0xFFFF_FFFF)  <-- uint.MaxValue
0            (0x0000_0000)  <-- оборачивание!
1            (0x0000_0001)
2            (0x0000_0002)
```

Рассмотрим ситуацию: RCV.NXT = 4294967294, receive window = 1024 байта. Допустимый диапазон sequence numbers: от 4294967294 до 4294967294 + 1024 = 1022 (с учётом оборачивания). Пришли два OOO-сегмента:

```
Сегмент X: SEQ = 4294967295  (перед оборачиванием)
Сегмент Y: SEQ = 5           (после оборачивания)

Правильный порядок: X, затем Y  (X ближе к RCV.NXT)

SortedDictionary<uint, ...>:
  Ключ 5           -> Y
  Ключ 4294967295  -> X

Порядок в дереве: Y (5), затем X (4294967295) -- НЕПРАВИЛЬНО!
```

`SortedDictionary<uint, ...>` использует стандартное числовое сравнение, при котором 5 < 4294967295. Но в TCP sequence space 5 *после* 4294967295 -- ведь мы прошли через оборот. Дерево поместит Y перед X, и вся логика обхода сломается: DrainContiguousData() попытается отдать Y раньше X, хотя между ними есть дыра.

Можно ли использовать кастомный компаратор с модульной арифметикой? Технически можно, но модульное сравнение не определяет полный порядок на всём множестве uint32: для трёх значений A, B, C может оказаться, что A < B < C < A (циклический порядок). `SortedDictionary` требует строгого полного порядка (транзитивности) -- иначе дерево будет давать неопределённые результаты.

Нам нужно другое решение.

---

## Часть 12.5 -- Абсолютная координата (64-bit unwrapping)

Решение элегантно: раз 32-битное пространство оборачивается, перейдём в 64-битное, которое не оборачивается (при реалистичных скоростях TCP-соединение закончится раньше, чем long переполнится).

Идея: храним внутренний счётчик `_absoluteRcvNxt` типа `long`, который начинается с значения ISN и монотонно растёт. При получении нового сегмента конвертируем его проволочный uint sequence number в абсолютную координату:

```
Проволока (uint):     4294967294  4294967295  0          1          2
Внутренне (long):     4294967294  4294967295  4294967296 4294967297 4294967298
                                              ^
                                       тут uint обернулся, long -- нет
```

Код конвертации:

```csharp
int relative = TcpSequence.Distance(RcvNxt, incomingSequence);
long absolute = _absoluteRcvNxt + relative;
```

`TcpSequence.Distance()` возвращает *знаковое* расстояние от RcvNxt до incomingSequence в модульном пространстве uint32. Это число типа `int` -- оно может быть отрицательным (сегмент из прошлого) или положительным (сегмент впереди). Прибавляя его к `_absoluteRcvNxt` (long), мы получаем абсолютную позицию, свободную от оборачивания.

Цена: одно вычитание (unchecked cast) и одно сложение на каждый вызов Push(). Выигрыш: все остальные структуры данных (SortedDictionary, сравнения, инварианты) работают с обычным long-порядком.

Почему не uint64 с явным отслеживанием числа оборотов? Потому что `TcpSequence.Distance()` уже содержит всю нужную математику. Дополнительный счётчик epoch -- это лишнее состояние, которое нужно синхронизировать.

---

## Часть 12.6 -- BufferedSegment: один диапазон в памяти

Каждый буферизованный out-of-order фрагмент представлен объектом `BufferedSegment`. Вот полный код:

```csharp
using System.Buffers;

namespace TcpOutOfOrderReassembly;

/// <summary>
/// Один сохранённый диапазон out-of-order данных: [Start, End).
/// Память арендуется через ArrayPool, чтобы не аллоцировать новый byte[]
/// на каждый пришедший не-по-порядку сегмент.
/// </summary>
internal sealed class BufferedSegment : IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private byte[]? _buffer;

    public BufferedSegment(
        long start,
        ReadOnlySpan<byte> data,
        ArrayPool<byte> pool)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (data.IsEmpty)
        {
            throw new ArgumentException(
                "Buffered segment cannot be empty.",
                nameof(data));
        }

        _pool = pool;
        _buffer = pool.Rent(data.Length);
        data.CopyTo(_buffer);
        Start = start;
        Length = data.Length;
    }

    /// <summary>Абсолютный, развёрнутый (не mod 2^32) sequence offset.</summary>
    public long Start { get; }

    public int Length { get; }

    public long End => checked(Start + Length);

    public ReadOnlySpan<byte> Data
    {
        get
        {
            byte[] buffer = _buffer
                ?? throw new ObjectDisposedException(nameof(BufferedSegment));
            return buffer.AsSpan(0, Length);
        }
    }

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _pool.Return(buffer, clearArray: false);
        }
    }
}
```

Разберём ключевые решения:

**Почему `long Start`, а не `uint Start`?**

Как мы выяснили в Части 12.5, внутри reassembler мы работаем в абсолютных 64-битных координатах. Start -- это абсолютная позиция первого байта данного диапазона в потоке. Использование long позволяет SortedDictionary сортировать диапазоны обычным числовым порядком без хаков с модульной арифметикой.

**Почему `ArrayPool<byte>`, а не просто `new byte[]`?**

В типичном сценарии с потерями пакетов reassembler создаёт и уничтожает буферы десятки и сотни раз в секунду. Каждый `new byte[]` -- это аллокация на управляемой куче, которая увеличивает давление на сборщик мусора. `ArrayPool<byte>` переиспользует массивы: после Dispose() массив возвращается в пул и может быть выдан заново при следующем Rent(). Это стандартная практика в высокопроизводительном .NET (Kestrel, ASP.NET Core, System.IO.Pipelines используют тот же подход).

**Почему `clearArray: false`?**

`ArrayPool.Return(buffer, clearArray: true)` обнуляет массив перед возвратом в пул -- защита от утечки данных из одного потребителя другому. Мы передаём `false`, потому что наши данные -- TCP payload, который не содержит секретов (шифрование обеспечивается уровнем выше -- TLS). При этом `clearArray: true` -- это memset на всю длину массива, что при тысячах вызовов в секунду заметно влияет на производительность.

**Почему `Interlocked.Exchange` в Dispose()?**

Паттерн `Interlocked.Exchange(ref _buffer, null)` решает две задачи:

1. Гарантирует, что повторный вызов Dispose() не вернёт один и тот же массив в пул дважды (двойной возврат испортил бы внутреннее состояние пула).
2. Является атомарной операцией -- даже если Dispose() случайно вызовут из двух потоков (что для нашего reassembler не должно случиться, но защита не помешает).

**Почему `End` вычисляется через `checked`?**

`checked(Start + Length)` бросит `OverflowException`, если сумма выходит за пределы long. Это невозможно в нормальной работе (потребовалось бы передать через одно соединение 2^63 байт), но checked-арифметика служит документацией намерения и страховочной сеткой.

---

## Часть 12.7 -- TcpSequence: арифметика (повторение из Модуля 11)

Для работы reassembler нужны операции над 32-битным модульным пространством TCP sequence numbers. Полный код вспомогательного класса:

```csharp
namespace TcpOutOfOrderReassembly;

/// <summary>
/// Операции над 32-битным TCP sequence space.
///
/// Сравнение корректно, когда расстояние между значениями
/// меньше 2^31, что обеспечивается ограниченным receive window.
/// </summary>
public static class TcpSequence
{
    public static bool LessThan(uint left, uint right)
        => unchecked((int)(left - right)) < 0;

    public static bool LessThanOrEqual(uint left, uint right)
        => left == right || LessThan(left, right);

    public static bool GreaterThan(uint left, uint right)
        => LessThan(right, left);

    public static bool GreaterThanOrEqual(uint left, uint right)
        => left == right || GreaterThan(left, right);

    /// <summary>
    /// Знаковое расстояние from -> to в модульном пространстве.
    ///
    /// Положительный результат означает, что to находится впереди from.
    /// Отрицательный — что to находится позади from.
    /// </summary>
    public static int Distance(uint from, uint to)
        => unchecked((int)(to - from));

    public static uint Add(uint sequenceNumber, int count)
        => unchecked(sequenceNumber + (uint)count);

    public static uint Add(uint sequenceNumber, uint count)
        => unchecked(sequenceNumber + count);
}
```

Этот класс подробно разобран в Модуле 11. Здесь кратко напомним ключевые моменты:

- `LessThan(a, b)` интерпретирует разность `a - b` как знаковое int32. Если результат отрицательный, значит `a` «левее» `b` в модульном пространстве. Это корректно, пока расстояние между a и b меньше 2^31 -- что гарантируется размером receive window (максимум 1 ГБ по RFC 7323).

- `Distance(from, to)` -- знаковое расстояние. Положительное значение означает, что `to` находится правее (дальше) `from`. Отрицательное -- что `to` в прошлом. Именно эту функцию мы используем в Push() для конвертации проволочного uint в абсолютную long-координату.

- `Add(seq, count)` -- модульное сложение через unchecked. Используется в Program.cs для вычисления sequence number по ISN + offset.

По сравнению с `SequenceMath` из Модуля 11, этот класс добавляет перегрузку `Add(uint, uint)` и переименован в `TcpSequence` для ясности.

---

## Часть 12.8 -- TcpStreamReassembler: полная реализация

Это центральный класс модуля -- полный reassembler одного направления TCP-соединения. Разберём его по частям, а затем приведём полный листинг.

### Поля и конструктор

```csharp
using System.Buffers;

namespace TcpOutOfOrderReassembly;

public delegate void TcpDataReadyHandler(ReadOnlySpan<byte> data);

public enum SegmentInsertResult
{
    Accepted,
    PartiallyAccepted,
    Duplicate,
    OutsideReceiveWindow,
    Empty,

    /// <summary>
    /// Сегмент создал бы новый диапазон, но буфер уже держит
    /// <see cref="TcpStreamReassembler.MaxBufferedRanges"/> диапазонов —
    /// защита от патологического трафика (тысячи однобайтовых
    /// сегментов через один, каждый создающий свой узел дерева и свой
    /// pooled-массив). Сегмент отброшен целиком, состояние не изменено.
    /// </summary>
    ResourceLimitExceeded
}
```

Делегат `TcpDataReadyHandler` принимает `ReadOnlySpan<byte>` -- данные, готовые для приложения. Span используется осознанно: он запрещает вызывающему сохранять ссылку на буфер (Span не может быть полем класса, элементом массива и т.д.), что позволяет reassembler безопасно переиспользовать память после возврата из callback.

`SegmentInsertResult` описывает все возможные исходы вставки сегмента:

| Значение | Что произошло |
|---|---|
| `Accepted` | Весь payload -- новые данные, все сохранены или доставлены |
| `PartiallyAccepted` | Часть payload была дубликатом или выходила за окно; новая часть принята |
| `Duplicate` | Все байты уже были получены ранее |
| `OutsideReceiveWindow` | Сегмент полностью за пределами receive window |
| `Empty` | Payload пуст (len=0) |
| `ResourceLimitExceeded` | Буфер переполнен по числу диапазонов -- DoS-защита |

```csharp
/// <summary>
/// Собирает TCP payload одного направления одного TCP-соединения.
/// Не thread-safe — обработку одного flow следует сериализовать.
///
/// Не reentrant: вызов <see cref="Push"/> изнутри переданного в другой
/// вызов Push колбэка onDataReady запрещён и приводит к исключению —
/// см. комментарий у <see cref="_isDelivering"/>.
/// </summary>
public sealed class TcpStreamReassembler : IDisposable
{
    private readonly SortedDictionary<long, BufferedSegment> _segments = new();
    private readonly ArrayPool<byte> _pool;
    private readonly int _receiveWindow;
    private readonly int _maxBufferedRanges;
    private long _absoluteRcvNxt;
    private long _bufferedBytes;
    private bool _disposed;
```

Обратите внимание: `_segments` -- это `SortedDictionary<long, BufferedSegment>`. Ключ -- `long` (абсолютная координата), не `uint`. Благодаря переходу в 64-битное пространство (Часть 12.5) стандартное числовое сравнение long дает правильный порядок.

```csharp
    /// <summary>
    /// true на время выполнения <see cref="DrainContiguousData"/>, то есть
    /// пока внутри выполняется вызванный пользователем onDataReady.
    /// Non-thread-safe не означает non-reentrant: без этой защиты callback
    /// теоретически мог бы дёрнуть Push повторно и получить тот же самый
    /// ещё-не-удалённый диапазон дважды.
    /// </summary>
    private bool _isDelivering;

    public TcpStreamReassembler(
        uint initialReceiveSequence,
        int receiveWindow = 4 * 1024 * 1024,
        int maxBufferedRanges = 4096,
        ArrayPool<byte>? pool = null)
    {
        if (receiveWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(receiveWindow),
                "Receive window must be positive.");
        if ((uint)receiveWindow >= 0x8000_0000u)
            throw new ArgumentOutOfRangeException(nameof(receiveWindow),
                "Receive window must be smaller than 2^31.");
        if (maxBufferedRanges <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedRanges),
                "Must allow at least one buffered range.");

        InitialReceiveSequence = initialReceiveSequence;
        _absoluteRcvNxt = initialReceiveSequence;
        _receiveWindow = receiveWindow;
        _maxBufferedRanges = maxBufferedRanges;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    public uint InitialReceiveSequence { get; }
    public uint RcvNxt => unchecked((uint)_absoluteRcvNxt);
    public int ReceiveWindow => _receiveWindow;
    public int MaxBufferedRanges => _maxBufferedRanges;
    public long BufferedBytes => _bufferedBytes;
    public int BufferedRangeCount => _segments.Count;
```

Конструктор проверяет инварианты:
- `receiveWindow` должен быть положительным и меньше 2^31 (половина uint32 пространства). Ограничение 2^31 гарантирует, что `TcpSequence.Distance()` даёт корректные результаты.
- `maxBufferedRanges` -- максимальное число диапазонов в буфере (DoS-защита, см. Часть 12.11).
- `pool` -- внешний ArrayPool или Shared по умолчанию.

Свойство `RcvNxt` конвертирует абсолютную координату обратно в проволочный uint32 через unchecked-приведение. Это нужно только для взаимодействия с внешним миром (генерация ACK).

### Push() -- главная точка входа

```csharp
    public SegmentInsertResult Push(
        uint sequenceNumber,
        ReadOnlySpan<byte> payload,
        TcpDataReadyHandler onDataReady)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onDataReady);

        // (1) Защита от реентрантности
        if (_isDelivering)
            throw new InvalidOperationException(
                "Reentrant Push: called from within a TcpDataReadyHandler " +
                "callback of another Push. Copy the data you need out of " +
                "the callback and call Push after it returns.");

        // (2) Пустой payload -- ничего делать не нужно
        if (payload.IsEmpty)
            return SegmentInsertResult.Empty;

        // (3) Конвертация wire uint -> absolute long
        int relativeStart = TcpSequence.Distance(RcvNxt, sequenceNumber);
        long segmentStart = checked(_absoluteRcvNxt + relativeStart);
        long segmentEnd = checked(segmentStart + payload.Length);

        // (4) Границы окна
        long windowStart = _absoluteRcvNxt;
        long windowEnd = checked(windowStart + _receiveWindow);

        // (5) Проверка: сегмент целиком в прошлом?
        if (segmentEnd <= windowStart)
            return SegmentInsertResult.Duplicate;

        // (6) Проверка: сегмент целиком за пределами окна?
        if (segmentStart >= windowEnd)
            return SegmentInsertResult.OutsideReceiveWindow;

        // (7) Обрезка слева (уже полученные байты)
        bool wasTrimmed = false;

        if (segmentStart < windowStart)
        {
            int trimLeft = checked((int)(windowStart - segmentStart));
            payload = payload[trimLeft..];
            segmentStart = windowStart;
            wasTrimmed = true;
        }

        // (8) Обрезка справа (за пределами окна)
        if (segmentEnd > windowEnd)
        {
            int acceptedLength = checked((int)(windowEnd - segmentStart));
            payload = payload[..acceptedLength];
            segmentEnd = windowEnd;
            wasTrimmed = true;
        }

        if (payload.IsEmpty)
            return wasTrimmed
                ? SegmentInsertResult.PartiallyAccepted
                : SegmentInsertResult.Duplicate;

        // (9) DoS-защита: лимит числа диапазонов
        if (_segments.Count >= _maxBufferedRanges)
            return SegmentInsertResult.ResourceLimitExceeded;

        // (10) Вставка только новых байт
        int insertedBytes = InsertOnlyNewRanges(segmentStart, payload);

        if (insertedBytes == 0)
            return SegmentInsertResult.Duplicate;

        // (11) Попытка доставить непрерывные данные начиная с RCV.NXT
        _isDelivering = true;
        try
        {
            DrainContiguousData(onDataReady);
        }
        finally
        {
            _isDelivering = false;
        }

        return wasTrimmed || insertedBytes != payload.Length
            ? SegmentInsertResult.PartiallyAccepted
            : SegmentInsertResult.Accepted;
    }
```

Пройдём по шагам:

**(1) Reentrancy guard.** Если мы уже внутри DrainContiguousData (выполняем callback пользователя), повторный вызов Push запрещён. Без этой защиты callback мог бы вставить новый сегмент, который DrainContiguousData тут же попытается доставить, создавая рекурсию и нарушая инварианты дерева. Подробнее -- в Части 12.10.

**(3) Конвертация координат.** `TcpSequence.Distance(RcvNxt, sequenceNumber)` возвращает знаковое расстояние от текущего RCV.NXT до пришедшего sequence number. Если сегмент из прошлого (retransmission уже доставленных данных), расстояние будет отрицательным, и `segmentStart` окажется меньше `_absoluteRcvNxt`, что будет поймано обрезкой на шаге (7).

**(5-6) Проверка окна.** Если весь сегмент левее windowStart -- это дубликат уже доставленных данных. Если правее windowEnd -- он вне окна (возможно, injection-атака или ошибка). Обе ситуации не требуют модификации буфера.

**(7-8) Обрезка.** Сегмент может частично попадать в окно. Левая обрезка отсекает уже доставленные байты, правая -- байты за пределами окна. После обрезки payload содержит только потенциально новые данные внутри окна.

**(9) DoS-защита.** Проверка выполняется _до_ InsertOnlyNewRanges, чтобы при превышении лимита не тратить ресурсы на анализ перекрытий. Подробнее -- Часть 12.11.

**(10-11) Вставка и доставка.** InsertOnlyNewRanges записывает в дерево только те байты, которых там ещё нет (overlap-trimming). DrainContiguousData проверяет, не образовался ли непрерывный поток от RCV.NXT, и если да -- доставляет всё, что можно.

### InsertOnlyNewRanges() -- алгоритм удаления перекрытий

Это ядро reassembler -- алгоритм, который принимает новый диапазон и вставляет в дерево только те подинтервалы, которых там ещё нет.

```csharp
    private int InsertOnlyNewRanges(
        long incomingStart,
        ReadOnlySpan<byte> incomingData)
    {
        long incomingEnd = checked(incomingStart + incomingData.Length);
        long cursor = incomingStart;
        int insertedBytes = 0;

        foreach ((long existingStart, BufferedSegment existing) in _segments)
        {
            long existingEnd = existing.End;

            // (A) Существующий сегмент полностью левее курсора -- пропускаем
            if (existingEnd <= cursor)
                continue;

            // (B) Существующий сегмент полностью правее входящего -- выходим
            if (existingStart >= incomingEnd)
                break;

            // (C) Между курсором и началом существующего есть щель -- вставляем
            if (existingStart > cursor)
            {
                long newRangeEnd = Math.Min(existingStart, incomingEnd);
                insertedBytes += AddRange(
                    incomingStart, cursor, newRangeEnd, incomingData);
                cursor = newRangeEnd;
            }

            // (D) Продвигаем курсор за конец существующего сегмента
            if (existingEnd > cursor)
                cursor = existingEnd;

            // (E) Если курсор дошёл до конца входящего -- больше нечего вставлять
            if (cursor >= incomingEnd)
                break;
        }

        // (F) Остаток после всех существующих сегментов
        if (cursor < incomingEnd)
            insertedBytes += AddRange(
                incomingStart, cursor, incomingEnd, incomingData);

        return insertedBytes;
    }
```

Проиллюстрируем работу на примере. Допустим, в буфере уже есть два диапазона:

```
Буфер:
  [100, 150)   -- сегмент E1 (50 байт)
  [200, 250)   -- сегмент E2 (50 байт)

Входящий: [120, 230)  -- 110 байт
```

Обход:

1. cursor = 120, incomingEnd = 230

2. Итерация 1: E1 = [100, 150)
   - existingEnd (150) > cursor (120) -- не пропускаем (A)
   - existingStart (100) < incomingEnd (230) -- не выходим (B)
   - existingStart (100) <= cursor (120) -- щели нет, (C) не срабатывает
   - existingEnd (150) > cursor (120) -- cursor = 150 (D)

3. Итерация 2: E2 = [200, 250)
   - existingEnd (250) > cursor (150) -- не пропускаем (A)
   - existingStart (200) < incomingEnd (230) -- не выходим (B)
   - existingStart (200) > cursor (150) -- есть щель [150, 200)! Вставляем. (C)
     - newRangeEnd = Min(200, 230) = 200
     - AddRange(120, 150, 200, data) -- 50 байт новых данных
     - cursor = 200
   - existingEnd (250) > cursor (200) -- cursor = 250 (D)
   - cursor (250) >= incomingEnd (230) -- break (E)

4. cursor (250) >= incomingEnd (230) -- (F) не срабатывает.

Результат: вставлен один новый диапазон [150, 200) с 50 байтами. Из 110 входящих байт 30 были дубликатами E1 (120..149), 50 -- новые (150..199), и 30 -- дубликаты E2 (200..229). Алгоритм вставил ровно 50 новых.

Вспомогательный метод AddRange вырезает нужный подинтервал из входящих данных, создаёт BufferedSegment и добавляет его в дерево:

```csharp
    private int AddRange(
        long incomingStart,
        long rangeStart,
        long rangeEnd,
        ReadOnlySpan<byte> incomingData)
    {
        int sourceOffset = checked((int)(rangeStart - incomingStart));
        int length = checked((int)(rangeEnd - rangeStart));

        if (length <= 0)
            return 0;

        ReadOnlySpan<byte> slice = incomingData.Slice(sourceOffset, length);
        var segment = new BufferedSegment(rangeStart, slice, _pool);

        try
        {
            _segments.Add(rangeStart, segment);
            _bufferedBytes += length;
            return length;
        }
        catch
        {
            segment.Dispose();
            throw;
        }
    }
```

Обратите внимание на try/catch: если `_segments.Add()` бросит исключение (например, ключ уже существует -- что не должно происходить при правильной логике), свежесозданный BufferedSegment будет освобождён, и арендованный массив вернётся в пул. Без этого при исключении мы получили бы утечку памяти пула.

### DrainContiguousData() -- доставка готовых данных

После каждой вставки нужно проверить: не образовалась ли непрерывная цепочка от RCV.NXT? Если первый сегмент в дереве начинается ровно с RCV.NXT, его можно отдать приложению, продвинуть RCV.NXT и проверить следующий.

```csharp
    /// <summary>
    /// Передаёт приложению все диапазоны, начинающиеся ровно с RCV.NXT.
    ///
    /// Порядок операций здесь принципиален: сегмент удаляется из дерева и
    /// возвращается в пул ТОЛЬКО ПОСЛЕ успешного возврата из onDataReady.
    /// Раньше было наоборот (remove -> try { onDataReady } finally { Dispose }),
    /// и если onDataReady бросало исключение, данные оказывались уже
    /// удалены из буфера и уже возвращены в пул, а RCV.NXT — не продвинут:
    /// главный инвариант ("RCV.NXT указывает на первый отсутствующий байт")
    /// оказывался нарушен, и эти байты терялись безвозвратно. Теперь при
    /// исключении сегмент остаётся на месте и будет предложен приложению
    /// заново при следующем вызове Push.
    /// </summary>
    private void DrainContiguousData(TcpDataReadyHandler onDataReady)
    {
        while (TryPeekFirstSegment(out long start, out BufferedSegment? segment))
        {
            if (start != _absoluteRcvNxt)
                return;

            // Сначала отдаём данные приложению
            onDataReady(segment.Data);

            // Если мы дошли до сюда, callback не бросил исключение —
            // теперь безопасно снять сегмент с учёта и сдвинуть RCV.NXT.
            bool removed = _segments.Remove(start);
            if (!removed)
                throw new InvalidOperationException(
                    "Reassembly queue was modified during delivery — " +
                    "this should be impossible given the reentrancy " +
                    "guard in Push().");

            _bufferedBytes -= segment.Length;
            _absoluteRcvNxt = checked(_absoluteRcvNxt + segment.Length);
            segment.Dispose();
        }
    }

    private bool TryPeekFirstSegment(
        out long start,
        out BufferedSegment? segment)
    {
        using SortedDictionary<long, BufferedSegment>.Enumerator enumerator =
            _segments.GetEnumerator();

        if (enumerator.MoveNext())
        {
            KeyValuePair<long, BufferedSegment> current = enumerator.Current;
            start = current.Key;
            segment = current.Value;
            return true;
        }

        start = default;
        segment = null;
        return false;
    }
```

Порядок операций в цикле критичен -- подробный разбор в Части 12.10.

`TryPeekFirstSegment()` использует GetEnumerator() вместо `_segments.First()` или LINQ, потому что:
- `First()` бросает исключение на пустой коллекции
- LINQ-методы аллоцируют
- Ручной enumerator -- O(log n) без аллокаций (struct enumerator SortedDictionary реализован через обход дерева)

### Dispose()

```csharp
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (BufferedSegment segment in _segments.Values)
            segment.Dispose();

        _segments.Clear();
        _bufferedBytes = 0;
    }
}
```

При уничтожении reassembler все буферизованные сегменты возвращаются в ArrayPool. Без этого -- утечка pooled-массивов.

### Полный листинг TcpStreamReassembler.cs

Для справки -- полный файл целиком, без разрывов:

```csharp
using System.Buffers;

namespace TcpOutOfOrderReassembly;

public delegate void TcpDataReadyHandler(ReadOnlySpan<byte> data);

public enum SegmentInsertResult
{
    Accepted,
    PartiallyAccepted,
    Duplicate,
    OutsideReceiveWindow,
    Empty,

    /// <summary>
    /// Сегмент создал бы новый диапазон, но буфер уже держит
    /// <see cref="TcpStreamReassembler.MaxBufferedRanges"/> диапазонов —
    /// защита от патологического трафика (тысячи однобайтовых
    /// сегментов через один, каждый создающий свой узел дерева и свой
    /// pooled-массив). Сегмент отброшен целиком, состояние не изменено.
    /// </summary>
    ResourceLimitExceeded
}

/// <summary>
/// Собирает TCP payload одного направления одного TCP-соединения.
/// Не thread-safe — обработку одного flow следует сериализовать.
///
/// Не reentrant: вызов <see cref="Push"/> изнутри переданного в другой
/// вызов Push колбэка onDataReady запрещён и приводит к исключению —
/// см. комментарий у <see cref="_isDelivering"/>.
/// </summary>
public sealed class TcpStreamReassembler : IDisposable
{
    private readonly SortedDictionary<long, BufferedSegment> _segments = new();
    private readonly ArrayPool<byte> _pool;
    private readonly int _receiveWindow;
    private readonly int _maxBufferedRanges;
    private long _absoluteRcvNxt;
    private long _bufferedBytes;
    private bool _disposed;

    /// <summary>
    /// true на время выполнения <see cref="DrainContiguousData"/>, то есть
    /// пока внутри выполняется вызванный пользователем onDataReady.
    /// Non-thread-safe не означает non-reentrant: без этой защиты callback
    /// теоретически мог бы дёрнуть Push повторно и получить тот же самый
    /// ещё-не-удалённый диапазон дважды.
    /// </summary>
    private bool _isDelivering;

    public TcpStreamReassembler(
        uint initialReceiveSequence,
        int receiveWindow = 4 * 1024 * 1024,
        int maxBufferedRanges = 4096,
        ArrayPool<byte>? pool = null)
    {
        if (receiveWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(receiveWindow),
                "Receive window must be positive.");
        if ((uint)receiveWindow >= 0x8000_0000u)
            throw new ArgumentOutOfRangeException(nameof(receiveWindow),
                "Receive window must be smaller than 2^31.");
        if (maxBufferedRanges <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedRanges),
                "Must allow at least one buffered range.");

        InitialReceiveSequence = initialReceiveSequence;
        _absoluteRcvNxt = initialReceiveSequence;
        _receiveWindow = receiveWindow;
        _maxBufferedRanges = maxBufferedRanges;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    public uint InitialReceiveSequence { get; }
    public uint RcvNxt => unchecked((uint)_absoluteRcvNxt);
    public int ReceiveWindow => _receiveWindow;
    public int MaxBufferedRanges => _maxBufferedRanges;
    public long BufferedBytes => _bufferedBytes;
    public int BufferedRangeCount => _segments.Count;

    public SegmentInsertResult Push(
        uint sequenceNumber,
        ReadOnlySpan<byte> payload,
        TcpDataReadyHandler onDataReady)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onDataReady);

        if (_isDelivering)
            throw new InvalidOperationException(
                "Reentrant Push: called from within a TcpDataReadyHandler " +
                "callback of another Push. Copy the data you need out of " +
                "the callback and call Push after it returns.");

        if (payload.IsEmpty)
            return SegmentInsertResult.Empty;

        int relativeStart = TcpSequence.Distance(RcvNxt, sequenceNumber);
        long segmentStart = checked(_absoluteRcvNxt + relativeStart);
        long segmentEnd = checked(segmentStart + payload.Length);

        long windowStart = _absoluteRcvNxt;
        long windowEnd = checked(windowStart + _receiveWindow);

        if (segmentEnd <= windowStart)
            return SegmentInsertResult.Duplicate;

        if (segmentStart >= windowEnd)
            return SegmentInsertResult.OutsideReceiveWindow;

        bool wasTrimmed = false;

        if (segmentStart < windowStart)
        {
            int trimLeft = checked((int)(windowStart - segmentStart));
            payload = payload[trimLeft..];
            segmentStart = windowStart;
            wasTrimmed = true;
        }

        if (segmentEnd > windowEnd)
        {
            int acceptedLength = checked((int)(windowEnd - segmentStart));
            payload = payload[..acceptedLength];
            segmentEnd = windowEnd;
            wasTrimmed = true;
        }

        if (payload.IsEmpty)
            return wasTrimmed
                ? SegmentInsertResult.PartiallyAccepted
                : SegmentInsertResult.Duplicate;

        // Грубая, консервативная защита: если фрагментов уже максимум,
        // отказываем целиком — даже если этот конкретный сегмент закрыл
        // бы существующие gaps и на самом деле уменьшил бы их число.
        // Правильнее было бы сначала прикинуть результирующее число
        // диапазонов и отклонять только когда оно останется выше лимита,
        // но для защиты от DoS "иногда отклонить полезный сегмент под
        // атакой" — приемлемый компромисс против "дать атакующему
        // неограниченно расти в узлах дерева и pooled-массивах".
        if (_segments.Count >= _maxBufferedRanges)
            return SegmentInsertResult.ResourceLimitExceeded;

        int insertedBytes = InsertOnlyNewRanges(segmentStart, payload);

        if (insertedBytes == 0)
            return SegmentInsertResult.Duplicate;

        _isDelivering = true;
        try
        {
            DrainContiguousData(onDataReady);
        }
        finally
        {
            _isDelivering = false;
        }

        return wasTrimmed || insertedBytes != payload.Length
            ? SegmentInsertResult.PartiallyAccepted
            : SegmentInsertResult.Accepted;
    }

    private int InsertOnlyNewRanges(
        long incomingStart,
        ReadOnlySpan<byte> incomingData)
    {
        long incomingEnd = checked(incomingStart + incomingData.Length);
        long cursor = incomingStart;
        int insertedBytes = 0;

        foreach ((long existingStart, BufferedSegment existing) in _segments)
        {
            long existingEnd = existing.End;

            if (existingEnd <= cursor)
                continue;

            if (existingStart >= incomingEnd)
                break;

            if (existingStart > cursor)
            {
                long newRangeEnd = Math.Min(existingStart, incomingEnd);
                insertedBytes += AddRange(
                    incomingStart, cursor, newRangeEnd, incomingData);
                cursor = newRangeEnd;
            }

            if (existingEnd > cursor)
                cursor = existingEnd;

            if (cursor >= incomingEnd)
                break;
        }

        if (cursor < incomingEnd)
            insertedBytes += AddRange(
                incomingStart, cursor, incomingEnd, incomingData);

        return insertedBytes;
    }

    private int AddRange(
        long incomingStart,
        long rangeStart,
        long rangeEnd,
        ReadOnlySpan<byte> incomingData)
    {
        int sourceOffset = checked((int)(rangeStart - incomingStart));
        int length = checked((int)(rangeEnd - rangeStart));

        if (length <= 0)
            return 0;

        ReadOnlySpan<byte> slice = incomingData.Slice(sourceOffset, length);
        var segment = new BufferedSegment(rangeStart, slice, _pool);

        try
        {
            _segments.Add(rangeStart, segment);
            _bufferedBytes += length;
            return length;
        }
        catch
        {
            segment.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Передаёт приложению все диапазоны, начинающиеся ровно с RCV.NXT.
    ///
    /// Порядок операций здесь принципиален: сегмент удаляется из дерева и
    /// возвращается в пул ТОЛЬКО ПОСЛЕ успешного возврата из onDataReady.
    /// Раньше было наоборот (remove -> try { onDataReady } finally { Dispose }),
    /// и если onDataReady бросало исключение, данные оказывались уже
    /// удалены из буфера и уже возвращены в пул, а RCV.NXT — не продвинут:
    /// главный инвариант ("RCV.NXT указывает на первый отсутствующий байт")
    /// оказывался нарушен, и эти байты терялись безвозвратно. Теперь при
    /// исключении сегмент остаётся на месте и будет предложен приложению
    /// заново при следующем вызове Push.
    /// </summary>
    private void DrainContiguousData(TcpDataReadyHandler onDataReady)
    {
        while (TryPeekFirstSegment(out long start, out BufferedSegment? segment))
        {
            if (start != _absoluteRcvNxt)
                return;

            onDataReady(segment.Data);

            // Если мы дошли до сюда, callback не бросил исключение —
            // теперь безопасно снять сегмент с учёта и сдвинуть RCV.NXT.
            bool removed = _segments.Remove(start);
            if (!removed)
                throw new InvalidOperationException(
                    "Reassembly queue was modified during delivery — " +
                    "this should be impossible given the reentrancy " +
                    "guard in Push().");

            _bufferedBytes -= segment.Length;
            _absoluteRcvNxt = checked(_absoluteRcvNxt + segment.Length);
            segment.Dispose();
        }
    }

    private bool TryPeekFirstSegment(
        out long start,
        out BufferedSegment? segment)
    {
        using SortedDictionary<long, BufferedSegment>.Enumerator enumerator =
            _segments.GetEnumerator();

        if (enumerator.MoveNext())
        {
            KeyValuePair<long, BufferedSegment> current = enumerator.Current;
            start = current.Key;
            segment = current.Value;
            return true;
        }

        start = default;
        segment = null;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (BufferedSegment segment in _segments.Values)
            segment.Dispose();

        _segments.Clear();
        _bufferedBytes = 0;
    }
}
```

---

## Часть 12.9 -- Три проверенных сценария

Теперь, когда мы разобрали все внутренности, посмотрим на reassembler в действии. Файл `Program.cs` содержит три сценария, каждый из которых проверяет отдельный аспект реализации.

### Полный код Program.cs

```csharp
using System.Text;
using TcpOutOfOrderReassembly;

RunReorderScenario();
Console.WriteLine();
RunOverlapScenario();
Console.WriteLine();
RunWrapAroundScenario();

return;

// -----------------------------------------------------------------
// Сценарий 1: сегменты приходят в произвольном порядке, один дубликат,
// один поздний ретрансмит уже доставленного диапазона.
//
// Ожидаемый поток: "Hello TCP out of order!"
// -----------------------------------------------------------------
static void RunReorderScenario()
{
    Console.WriteLine("=== Scenario 1: out-of-order + duplicates ===");

    const uint initialSequence = 1000;
    using var output = new MemoryStream();
    using var reassembler = new TcpStreamReassembler(
        initialReceiveSequence: initialSequence,
        receiveWindow: 64 * 1024);

    TcpDataReadyHandler consume = data =>
    {
        output.Write(data);
        Console.WriteLine(
            $"  -> application: \"{Encoding.ASCII.GetString(data)}\"");
    };

    // offset 0   "Hello "
    // offset 6   "TCP "
    // offset 10  "out "
    // offset 14  "of "
    // offset 17  "order!"
    Receive(reassembler, consume, initialSequence,
        offset: 17, text: "order!");   // пришёл первым
    Receive(reassembler, consume, initialSequence,
        offset: 10, text: "out ");     // gap ещё есть
    Receive(reassembler, consume, initialSequence,
        offset: 0,  text: "Hello ");   // доставится только "Hello "
    Receive(reassembler, consume, initialSequence,
        offset: 14, text: "of ");      // gap всё ещё 6..9
    Receive(reassembler, consume, initialSequence,
        offset: 6,  text: "TCP ");     // закрывает gap -> весь остаток
    Receive(reassembler, consume, initialSequence,
        offset: 6,  text: "TCP ");     // старый duplicate
    Receive(reassembler, consume, initialSequence,
        offset: 17, text: "order!");   // старый duplicate

    Console.WriteLine();
    Console.WriteLine(
        $"Final stream: \"{Encoding.ASCII.GetString(output.ToArray())}\"");
    Console.WriteLine($"RCV.NXT: {reassembler.RcvNxt}");
    Console.WriteLine($"Buffered bytes: {reassembler.BufferedBytes}");
    Console.WriteLine($"Buffered ranges: {reassembler.BufferedRangeCount}");
}

static void Receive(
    TcpStreamReassembler reassembler,
    TcpDataReadyHandler consume,
    uint initialSequence,
    int offset,
    string text)
{
    uint sequenceNumber = TcpSequence.Add(initialSequence, offset);
    byte[] payload = Encoding.ASCII.GetBytes(text);

    Console.WriteLine(
        $"RX SEQ={sequenceNumber}, offset={offset}, " +
        $"len={payload.Length}, data=\"{text}\"");

    SegmentInsertResult result = reassembler.Push(
        sequenceNumber, payload, consume);

    Console.WriteLine(
        $"  result={result}, RCV.NXT={reassembler.RcvNxt}, " +
        $"buffered={reassembler.BufferedBytes}");
}

// -----------------------------------------------------------------
// Сценарий 2: частичное перекрытие двух буферизованных диапазонов.
//
// [5005,5010) "FGHIJ"
// [5008,5013) "IJKLM"  <- только "KLM" новые, "IJ" уже сохранены
// [5000,5005) "ABCDE"  <- закрывает начальный gap, всё сливается
//
// Ожидаемый поток: "ABCDEFGHIJKLM"
// -----------------------------------------------------------------
static void RunOverlapScenario()
{
    Console.WriteLine("=== Scenario 2: partial overlap ===");

    using var output = new MemoryStream();
    using var reassembler = new TcpStreamReassembler(
        initialReceiveSequence: 5000,
        receiveWindow: 65_535);

    void Consume(ReadOnlySpan<byte> data)
    {
        output.Write(data);
        Console.WriteLine(
            $"  -> application: \"{Encoding.ASCII.GetString(data)}\"");
    }

    void Push(uint sequence, string text)
    {
        byte[] payload = Encoding.ASCII.GetBytes(text);
        Console.WriteLine(
            $"RX SEQ={sequence}, len={payload.Length}, data=\"{text}\"");
        SegmentInsertResult result = reassembler.Push(
            sequence, payload, Consume);
        Console.WriteLine(
            $"  result={result}, buffered={reassembler.BufferedBytes}, " +
            $"ranges={reassembler.BufferedRangeCount}");
    }

    Push(5005, "FGHIJ");
    Push(5008, "IJKLM");
    Push(5000, "ABCDE");

    Console.WriteLine();
    Console.WriteLine(
        $"Final stream: \"{Encoding.ASCII.GetString(output.ToArray())}\"");
}

// -----------------------------------------------------------------
// Сценарий 3: sequence number проходит через 2^32 - 1 -> 0 без
// каких-либо специальных ветвлений в основном алгоритме — вся
// развёртка происходит один раз, при вычислении relativeStart
// через TcpSequence.Distance.
// -----------------------------------------------------------------
static void RunWrapAroundScenario()
{
    Console.WriteLine("=== Scenario 3: sequence number wraparound ===");

    uint initialSequence = uint.MaxValue - 4;
    using var output = new MemoryStream();
    using var reassembler = new TcpStreamReassembler(
        initialSequence, receiveWindow: 1024);

    void Consume(ReadOnlySpan<byte> data) => output.Write(data);

    void Push(int offset, string text)
    {
        uint sequence = TcpSequence.Add(initialSequence, offset);
        Console.WriteLine($"offset={offset}, wire SEQ={sequence}");
        reassembler.Push(
            sequence, Encoding.ASCII.GetBytes(text), Consume);
    }

    Console.WriteLine($"initialSequence = {initialSequence}");
    Push(offset: 5, text: "WORLD");
    Push(offset: 0, text: "HELLO");

    Console.WriteLine(
        $"Final stream: " +
        $"\"{Encoding.ASCII.GetString(output.ToArray())}\"");
    Console.WriteLine($"RCV.NXT={reassembler.RcvNxt}");
}
```

### Сценарий 1: Reorder + duplicates

Что происходит:

```
RX SEQ=1017, "order!"    -> Accepted, буферизован в [1017,1023)
RX SEQ=1010, "out "      -> Accepted, буферизован в [1010,1014)
RX SEQ=1000, "Hello "    -> Accepted, начинается с RCV.NXT -> доставлен
                             -> application: "Hello "
                             RCV.NXT = 1006, в буфере [1010,1014) и [1017,1023)
RX SEQ=1014, "of "       -> Accepted, буферизован в [1014,1017)
                             Буфер: [1010,1014), [1014,1017), [1017,1023)
                             Но RCV.NXT=1006, а первый диапазон начинается с 1010 — дыра!
RX SEQ=1006, "TCP "      -> Accepted, начинается с RCV.NXT -> доставлен
                             -> application: "TCP "
                             RCV.NXT = 1010
                             Теперь [1010,1014) начинается с RCV.NXT -> доставлен
                             -> application: "out "
                             RCV.NXT = 1014
                             [1014,1017) начинается с RCV.NXT -> доставлен
                             -> application: "of "
                             RCV.NXT = 1017
                             [1017,1023) начинается с RCV.NXT -> доставлен
                             -> application: "order!"
                             RCV.NXT = 1023
RX SEQ=1006, "TCP "      -> Duplicate (все эти байты левее RCV.NXT)
RX SEQ=1017, "order!"    -> Duplicate
```

Финальный поток: `"Hello TCP out of order!"` -- ровно 23 байта в правильном порядке.

Ключевое наблюдение: один сегмент "TCP " (4 байта) закрыл единственную оставшуюся дыру и вызвал каскадную доставку четырёх диапазонов. DrainContiguousData() прошёл по цепочке [1006,1010) -> [1010,1014) -> [1014,1017) -> [1017,1023), отдавая каждый приложению и продвигая RCV.NXT.

### Сценарий 2: Partial overlap

```
RX SEQ=5005, "FGHIJ"    -> Accepted, буфер: [5005,5010)
RX SEQ=5008, "IJKLM"    -> PartiallyAccepted
                            InsertOnlyNewRanges видит:
                              cursor=5008, existing [5005,5010)
                              existingEnd (5010) > cursor (5008) -> cursor=5010
                              cursor (5010) < incomingEnd (5013) -> вставляем [5010,5013) "KLM"
                            Буфер: [5005,5010), [5010,5013)
                            "IJ" отброшены как дубликаты
RX SEQ=5000, "ABCDE"    -> Accepted, начинается с RCV.NXT (5000)
                            -> application: "ABCDE"
                            RCV.NXT = 5005
                            [5005,5010) начинается с RCV.NXT -> доставлен
                            -> application: "FGHIJ"
                            RCV.NXT = 5010
                            [5010,5013) начинается с RCV.NXT -> доставлен
                            -> application: "KLM"
                            RCV.NXT = 5013
```

Финальный поток: `"ABCDEFGHIJKLM"` -- 13 уникальных байт. Из 18 полученных ("FGHIJ" + "IJKLM" + "ABCDE") 5 были дубликатами ("IJ" из второго сегмента).

### Сценарий 3: Wrap-around

```
initialSequence = 4294967291 (uint.MaxValue - 4)

Push offset=5, wire SEQ = 4294967291 + 5 = 0 (обернулось!)
  -> "WORLD" буферизован

Push offset=0, wire SEQ = 4294967291
  -> "HELLO" начинается с RCV.NXT
  -> application: "HELLO"
  RCV.NXT продвигается на 5 байт
  -> [absoluteRcvNxt + 5] совпадает с началом "WORLD"
  -> application: "WORLD"
```

Финальный поток: `"HELLOWORLD"`. wire SEQ второго сегмента равен 0, но `TcpSequence.Distance(4294967291, 0)` корректно вычисляет расстояние 5, и абсолютная координата получается правильной. Никаких специальных ветвлений в коде -- оборачивание обрабатывается целиком в одной строке конвертации.

---

## Часть 12.10 -- Почему callback опасен

Казалось бы, доставка данных через callback -- тривиальная операция: вынул из дерева, отдал приложению, dispose. Но здесь скрывается коварный баг, который был обнаружен при code review. Разберём его подробно.

### Исходная (неправильная) реализация

Первая версия DrainContiguousData выглядела так:

```csharp
// НЕПРАВИЛЬНО -- баг с потерей данных при исключении
private void DrainContiguousData(TcpDataReadyHandler onDataReady)
{
    while (TryPeekFirstSegment(out long start, out BufferedSegment? segment))
    {
        if (start != _absoluteRcvNxt)
            return;

        _segments.Remove(start);          // (1) Удалили из дерева
        _bufferedBytes -= segment.Length;
        _absoluteRcvNxt += segment.Length; // (2) Сдвинули RCV.NXT

        try
        {
            onDataReady(segment.Data);     // (3) Отдали приложению
        }
        finally
        {
            segment.Dispose();             // (4) Вернули в пул
        }
    }
}
```

Что происходит, если `onDataReady` бросает исключение?

```
Шаг (1): _segments.Remove(start)     -- сегмент удалён из дерева
Шаг (2): _absoluteRcvNxt += length   -- RCV.NXT продвинут
Шаг (3): onDataReady(...)            -- ИСКЛЮЧЕНИЕ!
Шаг (4): segment.Dispose()           -- массив возвращён в пул
                                         (finally отработает в любом случае)

Результат:
  - Данные удалены из буфера (шаг 1)
  - RCV.NXT продвинут мимо них (шаг 2)
  - Массив уже в пуле, может быть перезаписан (шаг 4)
  - Приложение данные НЕ ПОЛУЧИЛО (шаг 3 не завершился)
  - Байты потеряны БЕЗВОЗВРАТНО
```

Это нарушает фундаментальный инвариант: «RCV.NXT указывает на первый отсутствующий байт». После исключения RCV.NXT указывает вперёд, но данных между старой и новой позицией нет ни в буфере, ни у приложения. Они просто исчезли.

### Правильный порядок операций

Исправленная версия (которую мы используем):

```
onDataReady(segment.Data)     -- (1) Сначала отдаём данные
                                     Если бросит -- сегмент остаётся в дереве,
                                     RCV.NXT не продвинут, данные не потеряны.
_segments.Remove(start)        -- (2) Успешно отдали -> удаляем
_bufferedBytes -= length       -- (3) Корректируем счётчик
_absoluteRcvNxt += length      -- (4) Продвигаем RCV.NXT
segment.Dispose()              -- (5) Возвращаем в пул
```

Если `onDataReady` бросает на шаге (1), ничего из шагов (2)-(5) не выполнится. Сегмент остаётся в дереве. При следующем вызове Push() (когда придёт новый сегмент) DrainContiguousData() снова увидит этот сегмент первым в дереве и попытается доставить повторно. Данные не потеряны.

### Reentrancy guard

Вторая опасность callback -- реентрантность. Что если пользовательский callback внутри `onDataReady` решит вызвать `Push()` с новым сегментом?

```csharp
reassembler.Push(seq1, data1, (ReadOnlySpan<byte> delivered) =>
{
    // Мы внутри DrainContiguousData()
    // Сегмент ещё не удалён из дерева!
    reassembler.Push(seq2, data2, ...);  // !!! Реентрантный вызов
});
```

Без защиты произойдёт следующее:

1. DrainContiguousData() выполняет onDataReady для сегмента S1
2. Callback вызывает Push() с S2
3. Push() вызывает InsertOnlyNewRanges() -- модифицирует _segments
4. Push() вызывает DrainContiguousData() -- начинает итерировать _segments
5. Внутренний DrainContiguousData() может снова увидеть S1 (он ещё не удалён!)
6. S1 будет доставлен повторно -- дубликат данных

Флаг `_isDelivering` предотвращает это: Push() проверяет флаг в самом начале и бросает `InvalidOperationException`, если он установлен.

```csharp
if (_isDelivering)
    throw new InvalidOperationException(
        "Reentrant Push: called from within a TcpDataReadyHandler " +
        "callback of another Push.");
```

Почему не `Monitor.Enter` (lock)? Потому что reassembler явно не thread-safe, и lock создал бы ложное ощущение потокобезопасности. Флаг _isDelivering -- это именно защита от реентрантности (один и тот же поток входит дважды), а не от concurrent access.

---

## Часть 12.11 -- DoS-защита: maxBufferedRanges

Receive window ограничивает общее количество буферизованных байт. Но он НЕ ограничивает количество отдельных диапазонов. Почему это проблема?

Рассмотрим атаку. Receive window = 65535 байт. Атакующий посылает:

```
SEQ = RCV.NXT + 0,  length = 1   -> байт 0
SEQ = RCV.NXT + 2,  length = 1   -> байт 2
SEQ = RCV.NXT + 4,  length = 1   -> байт 4
SEQ = RCV.NXT + 6,  length = 1   -> байт 6
...
SEQ = RCV.NXT + 65534, length = 1 -> байт 65534
```

Каждый сегмент -- 1 байт, и каждый чётный -- получен, каждый нечётный -- пропущен. Результат:

- Буферизовано всего 32768 байт -- в пределах receive window.
- Но это 32768 **отдельных диапазонов**, каждый -- отдельный узел SortedDictionary (красно-чёрное дерево) + отдельный BufferedSegment + отдельный pooled byte[].

Стоимость:
- 32768 узлов дерева: ~50 байт на узел (ключ long + указатели left/right/parent + цвет) = ~1.6 МБ overhead только на структуру дерева
- 32768 объектов BufferedSegment: ~40 байт каждый (ссылка на pool, buffer, Start, Length) = ~1.3 МБ
- 32768 pooled массивов: ArrayPool.Rent(1) вернёт минимум 16 байт = ~0.5 МБ
- Итого: ~3.4 МБ на 32 КБ полезных данных -- overhead 100x

И это на одно соединение. Если атакующий откроет 1000 таких соединений, это 3.4 ГБ overhead. При этом receive window вроде бы не превышен!

Параметр `maxBufferedRanges` ограничивает число диапазонов:

```csharp
if (_segments.Count >= _maxBufferedRanges)
    return SegmentInsertResult.ResourceLimitExceeded;
```

По умолчанию 4096 -- это покрывает реалистичные сценарии потерь (десятки одновременных gap'ов), но не позволяет создать десятки тысяч фрагментов.

Проверка намеренно грубая: если достигнут лимит, новый сегмент отклоняется целиком, даже если он мог бы закрыть существующие дыры и *уменьшить* число диапазонов. Это компромисс:

- **Точная проверка** требовала бы предварительного анализа перекрытий (фактически -- выполнения InsertOnlyNewRanges «насухую»), что удваивает вычислительную стоимость каждого Push().
- **Грубая проверка** иногда отклоняет полезный сегмент. Но отправитель ретранслирует его, и к тому времени другие дыры могут закрыться, снизив число диапазонов ниже лимита.
- Под DoS-атакой (патологический трафик) грубая проверка -- именно то, что нужно: быстрый reject без дорогостоящего анализа.

Для сравнения: Linux ядро ограничивает OOO-очередь через `tcp_max_reordering` (по умолчанию 300) и `sysctl net.ipv4.tcp_max_reordering`. FreeBSD использует `net.inet.tcp.reass.maxqueuelen` (по умолчанию 100).

---

## Часть 12.12 -- Эволюция: от Version 1 к production

Reassembler, который мы реализовали -- это Version 4 в нашей эволюционной линейке. Вот как он сюда дошёл и куда пойдёт дальше:

```
Version 1: работает (базовая реализация)
    |
    |  Проблема: смежные диапазоны не сливаются.
    |  [100,200) + [200,300) остаются двумя узлами.
    v
Version 2: merge adjacent ranges
    |  [100,200) + [200,300) -> [100,300)
    |  Уменьшает число узлов дерева, ускоряет DrainContiguousData()
    |
    |  Проблема: InsertOnlyNewRanges обходит всё дерево (O(n))
    v
Version 3: LowerBound вместо полного обхода
    |  Бинарный поиск стартовой позиции: O(log n) вместо O(n)
    |  SortedDictionary не поддерживает LowerBound напрямую --
    |  нужна кастомная SortedList или skip list
    |
    |  Проблема: нет защиты от DoS (неограниченное число диапазонов)
    v
Version 4: maxBufferedRanges (DoS protection) <-- МЫ ЗДЕСЬ
    |  Лимит на число буферизованных диапазонов.
    |  Наша реализация.
    |
    |  Вопрос: при перекрытии -- чьи данные оставить?
    v
Version 5: overlap policies (first/last data wins)
    |  RFC 9293 не предписывает, чьи данные оставить при перекрытии.
    |  "First wins" -- оставляем старые данные (наш подход).
    |  "Last wins" -- перезаписываем старые данные новыми.
    |  Для безопасности "first wins" предпочтительнее (атакующий
    |  не может подменить уже принятые данные).
    |
    |  Следующий шаг: сообщить отправителю, что мы получили
    v
Version 6: SACK generation
    |  Генерация SACK-блоков (RFC 2018) из буферизованных диапазонов.
    |  _segments.Keys даёт нам готовые диапазоны -- остаётся только
    |  конвертировать абсолютные координаты обратно в wire uint32
    |  и сериализовать в TCP options.
    |
    |  Финальный шаг: интеграция с остальным стеком
    v
Version 7: настоящий Receive Pipeline (Модуль 13)
    Reassembler становится частью полного receive path:
    Connection -> Receive Pipeline -> Reassembler -> Application
    Добавляются: управление окном, генерация ACK, flow control.
```

Каждая версия решает одну конкретную проблему. Мы остановились на Version 4, потому что она достаточна для корректной, безопасной работы. Версии 5-7 -- тема следующих модулей.

---

## Часть 12.13 -- Production Corner

Наша реализация -- минимальная корректная модель reassembly из RFC 9293 (секция 3.4). Что делают production-стеки поверх этого?

### Linux: красно-чёрное дерево

Linux хранит OOO-очередь в `struct tcp_sock`:

```c
// include/linux/tcp.h
struct tcp_sock {
    ...
    struct rb_root  out_of_order_queue;  // красно-чёрное дерево
    struct sk_buff  *ooo_last_skb;      // hint для быстрой вставки
    ...
};
```

Вместо SortedDictionary используется intrusive red-black tree (`rb_root`), где узлы встроены прямо в `sk_buff` (структуру сетевого буфера). Это даёт:
- Нулевой overhead аллокации: узлы дерева не создаются отдельно, а являются частью sk_buff
- O(log n) вставка и поиск
- `ooo_last_skb` -- оптимистичный hint: если новый сегмент идёт сразу после последнего вставленного (а так бывает часто), поиск в дереве вообще не нужен

### SACK Scoreboard (RFC 2018)

Selective Acknowledgment -- механизм, позволяющий получателю сообщить отправителю, какие именно диапазоны OOO-данных он получил. Без SACK отправитель знает только RCV.NXT (через cumulative ACK) и вынужден угадывать, что потерялось.

```
ACK header:
  ACK number = 1000  (cumulative: всё до 1000 получено)

SACK option:
  Block 1: [1500, 2000)  -- "у меня есть 1500-1999"
  Block 2: [2500, 3000)  -- "у меня есть 2500-2999"

Отправитель понимает: потеряны [1000,1500) и [2000,2500)
Ретранслирует только их, а не весь хвост с 1000.
```

В нашей реализации SACK-блоки можно сгенерировать из `_segments.Keys` -- это уже готовые непересекающиеся диапазоны в правильном порядке. Нужна только обратная конвертация из absolute long в wire uint32.

### DSACK (RFC 2883)

Duplicate SACK -- расширение SACK, которое сообщает отправителю о дубликатах. Если получатель видит данные, которые уже были доставлены (или уже буферизованы), он включает DSACK-блок в следующий ACK. Это помогает отправителю:
- Отличить потерю от переупорядочивания
- Калибровать retransmission timeout
- Обнаружить spurious retransmissions

### RACK (RFC 8985) -- Recent Acknowledgment

Механизм обнаружения потерь на основе временных меток, а не порядковых номеров. RACK отслеживает время отправки каждого сегмента и считает потерянным всё, что не было подтверждено в течение определённого интервала после самого последнего подтверждённого сегмента. Это более точный детектор потерь, чем классический алгоритм «три дубликата ACK» (fast retransmit).

### FACK -- Forward Acknowledgment (deprecated)

Экспериментальный механизм, использовавший самый правый SACK-блок как оценку «сколько данных в сети». Заменён RACK.

### PAWS (RFC 7323) -- Protection Against Wrapped Sequences

При высокоскоростных соединениях (10 Gbps+) 32-битные sequence numbers могут обернуться за секунды. Старый сегмент, задержавшийся в сети, может прийти с «правильным» sequence number, который уже обернулся и снова попал в окно. PAWS использует TCP timestamps для отклонения таких «антикварных» сегментов: если timestamp входящего сегмента меньше, чем у последнего принятого на этой позиции -- сегмент отбрасывается.

В нашей реализации PAWS не нужен: мы работаем на уровне учебного стека, где скорости далеки от 10 Gbps, и 32-битные sequence numbers оборачиваются за десятки секунд минимум (при MSS 1460 и окне 64 КБ).

### Итог

Наша реализация -- фундамент, на котором строятся все перечисленные механизмы. Она корректно обрабатывает произвольный порядок, дубликаты, перекрытия и оборачивание. Production-стеки добавляют поверх:

| Механизм | Что добавляет | RFC |
|---|---|---|
| SACK | Сообщает отправителю о полученных OOO-диапазонах | 2018 |
| DSACK | Сообщает о дубликатах | 2883 |
| RACK | Обнаружение потерь по времени | 8985 |
| PAWS | Защита от обёрнутых sequence numbers | 7323 |
| Merge adjacent | Уменьшает число узлов | - |

---

## Часть 12.14 -- Главный инвариант

После каждого вызова Push() должны выполняться шесть условий. Если хотя бы одно из них нарушено -- в reassembler баг.

### Инвариант 1: Все хранимые диапазоны правее RCV.NXT

```
Для каждого (start, segment) в _segments:
    start >= _absoluteRcvNxt
```

Если диапазон левее RCV.NXT -- значит, данные уже должны были быть доставлены приложению. Их наличие в буфере означает, что DrainContiguousData() не отработал до конца (баг) или что RCV.NXT откатился назад (катастрофический баг).

### Инвариант 2: Хранимые диапазоны не перекрываются

```
Для каждой пары соседних (s1, s2) в _segments (по ключу):
    s1.End <= s2.Start
```

InsertOnlyNewRanges() гарантирует, что вставляемые подинтервалы не пересекаются с существующими. Если перекрытие обнаружено -- баг в алгоритме обрезки.

### Инвариант 3: Нет уже-доставленных байт в буфере

Следует из инварианта 1: всё левее RCV.NXT считается доставленным, и ничего левее RCV.NXT в буфере нет.

### Инвариант 4: Диапазон, начинающийся с RCV.NXT, немедленно доставляется

```
Если _segments не пуст и первый ключ == _absoluteRcvNxt:
    НЕВОЗМОЖНО после Push()
```

DrainContiguousData() всегда вызывается после InsertOnlyNewRanges() и забирает все подряд идущие от RCV.NXT диапазоны. Единственное исключение -- если onDataReady бросил исключение: тогда диапазон остаётся, но это не нарушение инварианта, а отложенная повторная доставка.

### Инвариант 5: RCV.NXT указывает на первый отсутствующий байт

```
_absoluteRcvNxt == ISN + (число доставленных приложению байт)
```

RCV.NXT продвигается только в DrainContiguousData(), только после успешного вызова onDataReady, и ровно на длину доставленного диапазона. Если приложение получило N байт, RCV.NXT = ISN + N.

### Инвариант 6: BufferedBytes равен сумме длин всех хранимых диапазонов

```
_bufferedBytes == sum(segment.Length for segment in _segments.Values)
```

`_bufferedBytes` обновляется в двух местах: `AddRange()` (увеличивает) и `DrainContiguousData()` (уменьшает). Рассинхронизация означает баг в бухгалтерии.

### Зачем нужны инварианты?

Инварианты -- это не просто свойства, которые «так получаются». Это *контракт*, который reassembler обязуется поддерживать. Каждое изменение кода можно проверить вопросом: «Сохраняются ли все шесть инвариантов?». Баг в Части 12.10 (потеря данных при исключении в callback) был обнаружен именно так: ревьюер проверил инвариант 5 после исключения и обнаружил нарушение.

В production-коде инварианты часто реализуются как debug assertions:

```csharp
[Conditional("DEBUG")]
private void AssertInvariants()
{
    // Инвариант 1
    foreach (var (start, _) in _segments)
        Debug.Assert(start >= _absoluteRcvNxt);

    // Инвариант 2
    long? prevEnd = null;
    foreach (var (_, seg) in _segments)
    {
        if (prevEnd.HasValue)
            Debug.Assert(prevEnd.Value <= seg.Start);
        prevEnd = seg.End;
    }

    // Инвариант 6
    long sum = 0;
    foreach (var seg in _segments.Values)
        sum += seg.Length;
    Debug.Assert(sum == _bufferedBytes);
}
```

---

## Итоги модуля

В этом модуле мы:

1. Поняли, зачем нужен reassembler: TCP гарантирует байтовый поток, а IP доставляет пакеты в произвольном порядке.

2. Отвергли наивный подход (сортировка списка сегментов) и показали, почему нужен range-aware merging.

3. Выбрали полуоткрытые интервалы `[Start, End)` для представления диапазонов.

4. Решили проблему оборачивания uint32 через 64-bit unwrapping -- однократная конвертация при входе позволяет всем внутренним структурам работать с обычным long.

5. Реализовали BufferedSegment с ArrayPool<byte> для минимизации аллокаций.

6. Реализовали TcpStreamReassembler с тремя ключевыми алгоритмами:
   - Push() -- точка входа с проверками окна, обрезкой и DoS-защитой
   - InsertOnlyNewRanges() -- вставка с удалением перекрытий
   - DrainContiguousData() -- exception-safe доставка готовых данных

7. Обнаружили и исправили баг с потерей данных при исключении в callback.

8. Добавили DoS-защиту через maxBufferedRanges.

9. Наметили эволюционный путь к production-реализации.

10. Разобрали шесть инвариантов, которые гарантируют корректность.

В следующем модуле (Модуль 13) мы встроим reassembler в полный TCP Receive Pipeline, добавив управление окном, генерацию ACK и flow control.

---

> **Предыдущий модуль:** [Модуль 11: Sliding Window](Module-11-Sliding-Window-Implementation.md)  
> **Следующий модуль:** [Модуль 13: TCP Receive Pipeline](Module-13-TCP-Receive-Pipeline.md)
