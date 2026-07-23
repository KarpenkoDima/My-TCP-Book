using System.Buffers;
using System.Text;
using TcpOutOfOrderReassembly;
using Xunit;

namespace TcpOutOfOrderReassembly.Tests;

/// <summary>
/// Покрывает 20-пунктовый чек-лист сценариев для <see cref="TcpStreamReassembler"/>,
/// который обсуждался при разборе кода. Каждый тест назван и прокомментирован
/// так, чтобы явно указывать, какой пункт чек-листа он закрывает.
///
/// Каждое числовое значение sequence/offset/длины в этом файле было вручную
/// протрассировано по реализации перед написанием теста — namespace не
/// содержит "предположительных" assert-ов; если тест здесь падает при
/// реальном запуске, это либо ошибка в реализации, либо ошибка в этой
/// трассировке, а не расхождение по недосмотру.
/// </summary>
public sealed class TcpStreamReassemblerTests
{
    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static string ToAsciiString(ReadOnlySpan<byte> data) => Encoding.ASCII.GetString(data);

    /// <summary>
    /// Пушит текст как ASCII-байты и возвращает результат Push.
    /// Каждый доставленный callback'ом фрагмент добавляется в <paramref name="delivered"/>
    /// отдельным элементом — так тесты могут проверять и порядок доставки,
    /// и число вызовов callback'а, а не только итоговую конкатенацию.
    /// </summary>
    private static SegmentInsertResult PushText(
        TcpStreamReassembler reassembler,
        uint sequenceNumber,
        string text,
        List<string> delivered)
    {
        return reassembler.Push(
            sequenceNumber,
            Ascii(text),
            span => delivered.Add(ToAsciiString(span)));
    }

    // -----------------------------------------------------------------
    // 1. In-order segment
    // -----------------------------------------------------------------

    [Fact]
    public void Push_SegmentExactlyAtRcvNxt_IsDeliveredImmediately()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000);
        var delivered = new List<string>();

        SegmentInsertResult result = PushText(reassembler, 1000, "HELLO", delivered);

        Assert.Equal(SegmentInsertResult.Accepted, result);
        Assert.Equal(new[] { "HELLO" }, delivered);
        Assert.Equal(1005u, reassembler.RcvNxt);
        Assert.Equal(0, reassembler.BufferedRangeCount);
        Assert.Equal(0, reassembler.BufferedBytes);
    }

    // -----------------------------------------------------------------
    // 2. Two out-of-order segments
    // -----------------------------------------------------------------

    [Fact]
    public void Push_TwoOutOfOrderSegments_AreDeliveredInStreamOrderOnceGapCloses()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 5000);
        var delivered = new List<string>();

        // "WORLD" приходит первым, но занимает второе место в потоке (5005-5010).
        SegmentInsertResult first = PushText(reassembler, 5005, "WORLD", delivered);
        Assert.Equal(SegmentInsertResult.Accepted, first);
        Assert.Empty(delivered); // гэп 5000-5005 ещё не закрыт, ничего не доставлено
        Assert.Equal(1, reassembler.BufferedRangeCount);

        // "HELLO" закрывает гэп и тянет за собой уже буферизованный "WORLD".
        SegmentInsertResult second = PushText(reassembler, 5000, "HELLO", delivered);
        Assert.Equal(SegmentInsertResult.Accepted, second);

        Assert.Equal(new[] { "HELLO", "WORLD" }, delivered);
        Assert.Equal(5010u, reassembler.RcvNxt);
        Assert.Equal(0, reassembler.BufferedRangeCount);
    }

    // -----------------------------------------------------------------
    // 3. Full duplicate BEFORE delivery (сегмент ещё в out-of-order очереди)
    // -----------------------------------------------------------------

    [Fact]
    public void Push_ExactDuplicate_WhileStillBuffered_IsRejectedWithoutChangingState()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000);
        var delivered = new List<string>();

        // Гэп 1000-1005 не закрыт, поэтому "WORLD" остаётся в буфере, не доставляется.
        SegmentInsertResult first = PushText(reassembler, 1005, "WORLD", delivered);
        Assert.Equal(SegmentInsertResult.Accepted, first);
        Assert.Equal(1, reassembler.BufferedRangeCount);
        Assert.Equal(5, reassembler.BufferedBytes);

        // Тот же самый сегмент присылается повторно, пока он всё ещё в очереди.
        SegmentInsertResult duplicate = PushText(reassembler, 1005, "WORLD", delivered);

        Assert.Equal(SegmentInsertResult.Duplicate, duplicate);
        Assert.Empty(delivered);
        Assert.Equal(1, reassembler.BufferedRangeCount);
        Assert.Equal(5, reassembler.BufferedBytes);
    }

    // -----------------------------------------------------------------
    // 4. Full duplicate AFTER delivery (ретрансмит уже доставленных байт)
    // -----------------------------------------------------------------

    [Fact]
    public void Push_ExactDuplicate_AfterDelivery_HitsEarlyDuplicatePath()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000);
        var delivered = new List<string>();

        PushText(reassembler, 1000, "HELLO", delivered); // сразу доставлено, RCV.NXT = 1005
        delivered.Clear();

        // Ретрансмит уже подтверждённого сегмента: segmentEnd(1005) <= windowStart(1005)
        // — это отдельная, более ранняя ветка кода, чем "insertedBytes == 0" из теста 3.
        SegmentInsertResult duplicate = PushText(reassembler, 1000, "HELLO", delivered);

        Assert.Equal(SegmentInsertResult.Duplicate, duplicate);
        Assert.Empty(delivered);
        Assert.Equal(1005u, reassembler.RcvNxt);
        Assert.Equal(0, reassembler.BufferedRangeCount);
    }

    // -----------------------------------------------------------------
    // 5. Left overlap: новый сегмент перекрывает буферизованный слева
    // -----------------------------------------------------------------

    [Fact]
    public void Push_OverlappingExistingRangeOnTheLeft_KeepsOriginalBufferedBytes()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000);
        var delivered = new List<string>();

        // Буферизуем "FGHIJ" на 1005-1010 (гэп 1000-1005 ещё открыт).
        PushText(reassembler, 1005, "FGHIJ", delivered);

        // Новый сегмент 1000-1008 намеренно конфликтует на 1005-1008 ("XXX" вместо "FGH"),
        // чтобы доказать: побеждают уже сохранённые данные, а не новые перекрывающие.
        SegmentInsertResult result = PushText(reassembler, 1000, "ABCDEXXX", delivered);

        Assert.Equal(SegmentInsertResult.PartiallyAccepted, result); // вставлено только 5 из 8 байт
        Assert.Equal(new[] { "ABCDE", "FGHIJ" }, delivered);
        Assert.Equal("ABCDEFGHIJ", string.Concat(delivered));
        Assert.Equal(1010u, reassembler.RcvNxt);
        Assert.Equal(0, reassembler.BufferedRangeCount);
    }

    // -----------------------------------------------------------------
    // 6. Right overlap: новый сегмент перекрывает буферизованный справа
    // -----------------------------------------------------------------

    [Fact]
    public void Push_OverlappingExistingRangeOnTheRight_KeepsOriginalBufferedBytes()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 990);
        var delivered = new List<string>();

        // Буферизуем "ABCDE" на 1000-1005 (гэп 990-1000 ещё открыт).
        PushText(reassembler, 1000, "ABCDE", delivered);

        // Новый сегмент 1003-1008 конфликтует на 1003-1005 ("XY" вместо "DE") и
        // добавляет genuinely новый хвост 1005-1008 ("ZWQ").
        SegmentInsertResult overlapResult = PushText(reassembler, 1003, "XYZWQ", delivered);
        Assert.Equal(SegmentInsertResult.PartiallyAccepted, overlapResult); // вставлено 3 из 5 байт
        Assert.Empty(delivered); // гэп 990-1000 всё ещё открыт, ничего не доставлено

        // Закрываем начальный гэп — тянем за собой всё, что накопилось.
        SegmentInsertResult fillResult = PushText(reassembler, 990, "0123456789", delivered);

        Assert.Equal(SegmentInsertResult.Accepted, fillResult);
        Assert.Equal(new[] { "0123456789", "ABCDE", "ZWQ" }, delivered);
        Assert.Equal("0123456789ABCDEZWQ", string.Concat(delivered));
        Assert.Equal(1008u, reassembler.RcvNxt);
        Assert.Equal(0, reassembler.BufferedRangeCount);
    }

    // -----------------------------------------------------------------
    // 7. Сегмент, покрывающий сразу несколько существующих диапазонов
    // -----------------------------------------------------------------

    [Fact]
    public void Push_SegmentSpanningMultipleBufferedRanges_OnlyFillsGenuineGaps()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 2000);
        var delivered = new List<string>();

        PushText(reassembler, 2010, "AAAAA", delivered); // [2010,2015)
        PushText(reassembler, 2020, "BBBBB", delivered); // [2020,2025)
        Assert.Equal(2, reassembler.BufferedRangeCount);

        // Покрывает 2005-2023: закрывает гэп перед A (5 байт), гэп между A и B
        // (5 байт), и частично перекрывает начало B (это перекрытие отбрасывается).
        SegmentInsertResult bridging = PushText(
            reassembler, 2005, "XXXXXXXXXXXXXXXXXX", delivered); // 18 символов 'X'

        Assert.Equal(SegmentInsertResult.PartiallyAccepted, bridging); // вставлено 10 из 18 байт
        Assert.Equal(4, reassembler.BufferedRangeCount); // A, B и два новых X-диапазона, не слиты
        Assert.Empty(delivered); // гэп 2000-2005 всё ещё открыт

        PushText(reassembler, 2000, "00000", delivered); // закрывает финальный гэп

        Assert.Equal(
            new[] { "00000", "XXXXX", "AAAAA", "XXXXX", "BBBBB" },
            delivered);
        Assert.Equal("00000XXXXXAAAAAXXXXXBBBBB", string.Concat(delivered));
        Assert.Equal(0, reassembler.BufferedRangeCount);
        Assert.Equal(2025u, reassembler.RcvNxt);
    }

    // -----------------------------------------------------------------
    // 8. Существующий диапазон полностью покрывает входящий сегмент
    // -----------------------------------------------------------------

    [Fact]
    public void Push_SegmentFullyCoveredByExistingRange_IsRejectedAsDuplicate()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1); // гэп навсегда открыт
        var delivered = new List<string>();

        PushText(reassembler, 1000, "HELLOWORLD", delivered); // [1000,1010)
        Assert.Equal(1, reassembler.BufferedRangeCount);
        Assert.Equal(10, reassembler.BufferedBytes);

        // [1002,1005) целиком внутри уже сохранённого [1000,1010).
        SegmentInsertResult result = PushText(reassembler, 1002, "XXX", delivered);

        Assert.Equal(SegmentInsertResult.Duplicate, result);
        Assert.Equal(1, reassembler.BufferedRangeCount);
        Assert.Equal(10, reassembler.BufferedBytes); // не выросло — новых байт не вставлено
    }

    // -----------------------------------------------------------------
    // 9. Входящий сегмент полностью покрывает существующий диапазон
    // -----------------------------------------------------------------

    [Fact]
    public void Push_SegmentFullyCoveringExistingRange_PreservesOriginalBytesInTheMiddle()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1);
        var delivered = new List<string>();

        PushText(reassembler, 1002, "XXX", delivered); // [1002,1005)
        Assert.Equal(1, reassembler.BufferedRangeCount);

        // [1000,1010) целиком накрывает существующий [1002,1005) с обеих сторон.
        SegmentInsertResult result = PushText(reassembler, 1000, "0123456789", delivered);

        Assert.Equal(SegmentInsertResult.PartiallyAccepted, result); // вставлено 7 из 10 байт
        Assert.Equal(3, reassembler.BufferedRangeCount); // [1000,1002) + старый [1002,1005) + [1005,1010)
        Assert.Equal(10, reassembler.BufferedBytes); // 2 + 3 + 5
    }

    // -----------------------------------------------------------------
    // 10. Left receive-window trim
    // -----------------------------------------------------------------

    [Fact]
    public void Push_SegmentStartingBeforeReceiveWindow_TrimsAlreadyDeliveredPrefix()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000);
        var delivered = new List<string>();

        // Сегмент [995,1005): "AAAAA" (995-1000) уже "старое" — до RCV.NXT,
        // "BBBBB" (1000-1005) — новое и попадает точно в RCV.NXT.
        uint seq = TcpSequence.Add(1000u, -5);
        SegmentInsertResult result = PushText(reassembler, seq, "AAAAABBBBB", delivered);

        Assert.Equal(SegmentInsertResult.PartiallyAccepted, result);
        Assert.Equal(new[] { "BBBBB" }, delivered); // только новая, не обрезанная часть
        Assert.Equal(1005u, reassembler.RcvNxt);
        Assert.Equal(0, reassembler.BufferedRangeCount);
    }

    // -----------------------------------------------------------------
    // 11. Right receive-window trim
    // -----------------------------------------------------------------

    [Fact]
    public void Push_SegmentExtendingPastReceiveWindow_TrimsTail()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000, receiveWindow: 10);
        var delivered = new List<string>();

        // windowEnd = 1010. Сегмент [1005,1015) должен обрезаться до [1005,1010).
        SegmentInsertResult result = PushText(reassembler, 1005, "AAAAABBBBB", delivered); // 10 символов

        Assert.Equal(SegmentInsertResult.PartiallyAccepted, result);
        Assert.Empty(delivered); // гэп 1000-1005 не закрыт
        Assert.Equal(1, reassembler.BufferedRangeCount);
        Assert.Equal(5, reassembler.BufferedBytes); // только первые 5 байт вошли в окно
    }

    // -----------------------------------------------------------------
    // 12. Сегмент целиком за правым краем receive window
    // -----------------------------------------------------------------

    [Fact]
    public void Push_SegmentEntirelyOutsideReceiveWindow_IsRejectedWithoutSideEffects()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000, receiveWindow: 10);
        var delivered = new List<string>();

        // windowEnd = 1010; segmentStart = 1010 >= windowEnd.
        SegmentInsertResult result = PushText(reassembler, 1010, "X", delivered);

        Assert.Equal(SegmentInsertResult.OutsideReceiveWindow, result);
        Assert.Empty(delivered);
        Assert.Equal(0, reassembler.BufferedRangeCount);
        Assert.Equal(0, reassembler.BufferedBytes);
        Assert.Equal(1000u, reassembler.RcvNxt);
    }

    // -----------------------------------------------------------------
    // 13. Wrap-around: одиночный сегмент пересекает 2^32-1 -> 0
    // -----------------------------------------------------------------

    [Fact]
    public void Push_SingleSegmentCrossingSequenceWraparound_AdvancesRcvNxtCorrectly()
    {
        uint initial = uint.MaxValue - 2; // 4294967293
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: initial);
        var delivered = new List<string>();

        // "ABCDE" (5 байт) начинается на 4294967293 и пересекает границу 2^32.
        SegmentInsertResult result = PushText(reassembler, initial, "ABCDE", delivered);

        Assert.Equal(SegmentInsertResult.Accepted, result);
        Assert.Equal(new[] { "ABCDE" }, delivered);
        Assert.Equal(2u, reassembler.RcvNxt); // (4294967293 + 5) mod 2^32 = 2
        Assert.Equal(0, reassembler.BufferedRangeCount);
    }

    // -----------------------------------------------------------------
    // 14. Wrap-around с несколькими out-of-order сегментами
    // -----------------------------------------------------------------

    [Fact]
    public void Push_MultipleOutOfOrderSegmentsAcrossWraparound_ReassembleCorrectly()
    {
        uint initial = uint.MaxValue - 4; // 4294967291
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: initial);
        var delivered = new List<string>();

        uint worldSeq = TcpSequence.Add(initial, 5); // wraps to 0
        PushText(reassembler, worldSeq, "WORLD", delivered); // приходит первым, но идёт вторым в потоке
        Assert.Empty(delivered);
        Assert.Equal(1, reassembler.BufferedRangeCount);

        PushText(reassembler, initial, "HELLO", delivered); // закрывает гэп, тянет "WORLD" за собой

        Assert.Equal(new[] { "HELLO", "WORLD" }, delivered);
        Assert.Equal("HELLOWORLD", string.Concat(delivered));
        Assert.Equal(5u, reassembler.RcvNxt); // (4294967291 + 10) mod 2^32 = 5
        Assert.Equal(0, reassembler.BufferedRangeCount);
    }

    // -----------------------------------------------------------------
    // 15. Callback бросает исключение
    // -----------------------------------------------------------------

    [Fact]
    public void Push_WhenCallbackThrows_SegmentIsNotLostAndGuardIsReset()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000);

        // Callback бросает исключение при доставке. Раньше (до код-ревью)
        // сегмент к этому моменту уже удалялся из дерева и возвращался в pool
        // ДО вызова callback'а — то есть данные терялись безвозвратно.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            reassembler.Push(1000, Ascii("HELLO"), _ =>
                throw new InvalidOperationException("simulated consumer failure")));
        Assert.Equal("simulated consumer failure", ex.Message);

        // Инвариант должен быть сохранён: сегмент НЕ удалён, RCV.NXT НЕ сдвинут,
        // байты по-прежнему физически лежат в буфере.
        Assert.Equal(1000u, reassembler.RcvNxt);
        Assert.Equal(1, reassembler.BufferedRangeCount);
        Assert.Equal(5, reassembler.BufferedBytes);

        // Повторный Push с тем же сегментом обязан не упасть на ложном
        // "Reentrant Push" (флаг _isDelivering должен быть сброшен в finally
        // после первого, упавшего, вызова) — и обязан вернуть Duplicate,
        // а не тихо продублировать доставку: сегмент по-прежнему целиком
        // совпадает с уже сохранённым в дереве диапазоном.
        var delivered = new List<string>();
        SegmentInsertResult retryResult = PushText(reassembler, 1000, "HELLO", delivered);

        Assert.Equal(SegmentInsertResult.Duplicate, retryResult);
        Assert.Empty(delivered);
        Assert.Equal(1, reassembler.BufferedRangeCount); // данные всё ещё на месте, не потеряны
    }

    // -----------------------------------------------------------------
    // 16. Callback реентерабельно вызывает Push
    // -----------------------------------------------------------------

    [Fact]
    public void Push_WhenCallbackCallsPushReentrantly_ThrowsAndPreservesState()
    {
        using var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            reassembler.Push(1000, Ascii("HELLO"), _ =>
            {
                // Попытка повторно войти в Push прямо из callback'а.
                reassembler.Push(2000, Ascii("X"), __ => { });
            }));

        Assert.Contains("Reentrant Push", ex.Message);

        // Сегмент так и не был успешно доставлен (callback не вернулся штатно),
        // поэтому он остаётся в очереди, а RCV.NXT не сдвигается.
        Assert.Equal(1000u, reassembler.RcvNxt);
        Assert.Equal(1, reassembler.BufferedRangeCount);

        // Флаг _isDelivering должен быть сброшен в finally — следующий обычный
        // Push обязан отработать без ложного "Reentrant Push".
        var delivered = new List<string>();
        SegmentInsertResult afterward = PushText(reassembler, 1000, "HELLO", delivered);
        Assert.Equal(SegmentInsertResult.Duplicate, afterward); // тот же сегмент уже лежит в дереве
    }

    // -----------------------------------------------------------------
    // 17. Dispose с ещё буферизованными (недоставленными) сегментами
    // -----------------------------------------------------------------

    [Fact]
    public void Dispose_WithBufferedSegments_ReturnsAllRentedArraysToPool()
    {
        var pool = new CountingArrayPool();
        var reassembler = new TcpStreamReassembler(
            initialReceiveSequence: 1000,
            pool: pool);

        // Оба сегмента остаются в out-of-order очереди — гэп 1000-1005 не закрыт,
        // "WORLD" на 1010 отдельно не связан c "MIDDLE" на 1005.
        reassembler.Push(1005, Ascii("MIDDL"), _ => { });
        reassembler.Push(1010, Ascii("WORLD"), _ => { });

        Assert.Equal(2, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);

        reassembler.Dispose();

        Assert.Equal(2, pool.ReturnCount); // оба арендованных массива возвращены

        // Dispose должен быть идемпотентным — повторный вызов не бросает
        // и не пытается вернуть те же массивы в пул повторно.
        reassembler.Dispose();
        Assert.Equal(2, pool.ReturnCount);
    }

    // -----------------------------------------------------------------
    // 18. Push после Dispose
    // -----------------------------------------------------------------

    [Fact]
    public void Push_AfterDispose_ThrowsObjectDisposedException()
    {
        var reassembler = new TcpStreamReassembler(initialReceiveSequence: 1000);
        reassembler.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            reassembler.Push(1000, Ascii("X"), _ => { }));
    }

    // -----------------------------------------------------------------
    // 19. Fragment-count limit (MaxBufferedRanges)
    // -----------------------------------------------------------------

    [Fact]
    public void Push_BeyondMaxBufferedRanges_IsRejectedEvenWhenItWouldReduceFragmentation()
    {
        using var reassembler = new TcpStreamReassembler(
            initialReceiveSequence: 100,
            receiveWindow: 100_000,
            maxBufferedRanges: 2);

        PushText(reassembler, 110, "AA", new List<string>()); // [110,112)
        PushText(reassembler, 120, "BB", new List<string>()); // [120,122)
        Assert.Equal(2, reassembler.BufferedRangeCount);

        // Простой случай: третий, никак не связанный диапазон отклоняется.
        SegmentInsertResult rejected = PushText(reassembler, 130, "CC", new List<string>());
        Assert.Equal(SegmentInsertResult.ResourceLimitExceeded, rejected);
        Assert.Equal(2, reassembler.BufferedRangeCount);

        // Показательный случай (задокументирован в комментарии к самому коду):
        // сегмент [100,110), который закрыл бы начальный гэп и СРАЗУ ЖЕ утянул
        // бы за собой диапазон [110,112) через Drain — то есть чистый эффект
        // был бы УМЕНЬШЕНИЕМ числа диапазонов с 2 до 1 — всё равно отклоняется,
        // потому что проверка лимита выполняется ДО вставки, по количеству ДО
        // операции, а не по прогнозируемому результату. Это осознанный, а не
        // случайный компромисс — тест фиксирует его, чтобы поведение не
        // "починили" незаметно при рефакторинге.
        var delivered = new List<string>();
        SegmentInsertResult wouldHaveHelped = PushText(reassembler, 100, "0123456789", delivered);

        Assert.Equal(SegmentInsertResult.ResourceLimitExceeded, wouldHaveHelped);
        Assert.Empty(delivered);
        Assert.Equal(2, reassembler.BufferedRangeCount); // НЕ уменьшилось до 1
    }

    // -----------------------------------------------------------------
    // 20. Randomized / property-based reassembly
    // -----------------------------------------------------------------

    /// <summary>
    /// Не использует внешнюю property-based библиотеку (например, FsCheck),
    /// чтобы не добавлять лишнюю зависимость ради одного теста — вместо этого
    /// несколько фиксированных seed'ов дают детерминированное, воспроизводимое
    /// покрытие. При желании можно заменить на честный property-based тест.
    ///
    /// Область охвата: непересекающиеся чанки случайного размера, случайный
    /// порядок доставки, случайные повторные (дублирующие) отправки уже
    /// виденных чанков. Конфликтующие перекрытия (разные данные на одном и
    /// том же диапазоне) здесь намеренно НЕ генерируются — это уже точечно
    /// проверено тестами 5-9; здесь цель — доказать корректность на масштабе,
    /// а не повторно проверять overlap-политику.
    /// </summary>
    [Theory]
    [InlineData(12345)]
    [InlineData(999)]
    [InlineData(424242)]
    public void RandomizedChunkingAndShuffling_AlwaysReassemblesExactOriginalStream(int seed)
    {
        var random = new Random(seed);

        byte[] reference = new byte[2000];
        random.NextBytes(reference);

        const uint initialSequence = 1_000_000;

        List<(int Offset, byte[] Data)> chunks = SplitIntoRandomChunks(reference, random, maxChunkSize: 64);
        Shuffle(chunks, random);

        using var output = new MemoryStream();
        using var reassembler = new TcpStreamReassembler(
            initialReceiveSequence: initialSequence,
            receiveWindow: reference.Length + 1024);

        foreach ((int offset, byte[] data) in chunks)
        {
            uint seq = TcpSequence.Add(initialSequence, offset);

            reassembler.Push(seq, data, span => output.Write(span));

            // Примерно каждый 4-й чанк отправляем повторно — должен быть
            // молча (без побочных эффектов) отклонён как Duplicate в одной
            // из двух ранних веток кода, независимо от того, был ли он уже
            // доставлен или всё ещё лежит в out-of-order очереди.
            if (random.Next(4) == 0)
            {
                reassembler.Push(seq, data, span => output.Write(span));
            }
        }

        Assert.Equal(reference, output.ToArray());
        Assert.Equal(0, reassembler.BufferedRangeCount);
        Assert.Equal(0, reassembler.BufferedBytes);
        Assert.Equal(unchecked(initialSequence + (uint)reference.Length), reassembler.RcvNxt);
    }

    private static List<(int Offset, byte[] Data)> SplitIntoRandomChunks(
        byte[] source,
        Random random,
        int maxChunkSize)
    {
        var chunks = new List<(int, byte[])>();
        int offset = 0;

        while (offset < source.Length)
        {
            int remaining = source.Length - offset;
            int size = Math.Min(remaining, random.Next(1, maxChunkSize + 1));

            var data = new byte[size];
            Array.Copy(source, offset, data, 0, size);

            chunks.Add((offset, data));
            offset += size;
        }

        return chunks;
    }

    private static void Shuffle<T>(IList<T> list, Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Тестовый ArrayPool, который считает Rent/Return, чтобы проверить,
    /// что Dispose реассемблера действительно возвращает все арендованные
    /// массивы, а не только освобождает managed-обёртки вокруг них.
    /// </summary>
    private sealed class CountingArrayPool : ArrayPool<byte>
    {
        private readonly ArrayPool<byte> _inner = Create();

        public int RentCount { get; private set; }
        public int ReturnCount { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            RentCount++;
            return _inner.Rent(minimumLength);
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ReturnCount++;
            _inner.Return(array, clearArray);
        }
    }
}
