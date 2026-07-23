using System.Buffers;

namespace TcpReceivePipeline.Core;

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
            throw new ArgumentOutOfRangeException(nameof(receiveWindow), "Receive window must be positive.");
        if ((uint)receiveWindow >= 0x8000_0000u)
            throw new ArgumentOutOfRangeException(nameof(receiveWindow), "Receive window must be smaller than 2^31.");
        if (maxBufferedRanges <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedRanges), "Must allow at least one buffered range.");

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

    public SegmentInsertResult Push(uint sequenceNumber, ReadOnlySpan<byte> payload, TcpDataReadyHandler onDataReady)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onDataReady);

        if (_isDelivering)
            throw new InvalidOperationException(
                "Reentrant Push: called from within a TcpDataReadyHandler callback of another Push. " +
                "Copy the data you need out of the callback and call Push after it returns.");

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
            return wasTrimmed ? SegmentInsertResult.PartiallyAccepted : SegmentInsertResult.Duplicate;

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

    private int InsertOnlyNewRanges(long incomingStart, ReadOnlySpan<byte> incomingData)
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
                insertedBytes += AddRange(incomingStart, cursor, newRangeEnd, incomingData);
                cursor = newRangeEnd;
            }

            if (existingEnd > cursor)
                cursor = existingEnd;

            if (cursor >= incomingEnd)
                break;
        }

        if (cursor < incomingEnd)
            insertedBytes += AddRange(incomingStart, cursor, incomingEnd, incomingData);

        return insertedBytes;
    }

    private int AddRange(long incomingStart, long rangeStart, long rangeEnd, ReadOnlySpan<byte> incomingData)
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
                    "Reassembly queue was modified during delivery — this should be impossible " +
                    "given the reentrancy guard in Push().");

            _bufferedBytes -= segment.Length;
            _absoluteRcvNxt = checked(_absoluteRcvNxt + segment.Length);
            segment.Dispose();
        }
    }

    private bool TryPeekFirstSegment(out long start, out BufferedSegment? segment)
    {
        using SortedDictionary<long, BufferedSegment>.Enumerator enumerator = _segments.GetEnumerator();

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
