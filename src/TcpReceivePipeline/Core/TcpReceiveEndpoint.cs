using System.Buffers;

namespace TcpReceivePipeline.Core;

/// <summary>
/// Полная receive-сторона одного направления TCP-соединения: reassembly
/// (через <see cref="TcpStreamReassembler"/>) + честный расчёт RCV.WND.
///
/// Ключевое отличие от "наивного" приёмника из первой версии: там
/// RCV.WND был статическим числом, а RCV.NXT — отдельным полем, которое
/// нужно было вручную синхронизировать с тем, что реально доставлено.
/// Здесь RCV.NXT — это <see cref="TcpStreamReassembler.RcvNxt"/> напрямую
/// (единственный источник истины), а RCV.WND считается как
/// "ёмкость минус уже забуференные байты" — включая байты, которые лежат
/// в out-of-order очереди и *ещё не переданы приложению*. Именно они,
/// а не только неподтверждённые данные, отъедают память получателя.
/// </summary>
public sealed class TcpReceiveEndpoint : IDisposable
{
    private readonly TcpStreamReassembler _reassembler;
    private readonly int _capacity;

    public TcpReceiveEndpoint(uint initialReceiveSequence, int capacity, int maxBufferedRanges = 4096, ArrayPool<byte>? pool = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _reassembler = new TcpStreamReassembler(
            initialReceiveSequence,
            receiveWindow: capacity,
            maxBufferedRanges: maxBufferedRanges,
            pool: pool);
    }

    /// <summary>Следующий байт, который мы ожидаем — источник истины, не дублируется отдельным полем.</summary>
    public uint RcvNxt => _reassembler.RcvNxt;

    public long BufferedBytes => _reassembler.BufferedBytes;
    public int BufferedRangeCount => _reassembler.BufferedRangeCount;

    /// <summary>
    /// Реально рекламируемое окно: ёмкость минус занятая память
    /// (contiguous-доставка ещё не произошла, но байты уже физически
    /// в буфере). Ограничено 16 битами — таков wire-формат окна в
    /// заголовке TCP (RFC 9293 §3.1); при масштабировании (Window Scale)
    /// сюда добавился бы сдвиг, но это отдельный слой (см. §15 прошлого
    /// материала).
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
    /// Принимает один входящий сегмент. onDataReady вызывается синхронно
    /// для каждого непрерывного диапазона, ставшего доступным — в том
    /// числе каскадно, если этот сегмент закрыл сразу несколько gap'ов.
    /// </summary>
    public SegmentInsertResult Receive(TcpSegment segment, TcpDataReadyHandler onDataReady)
        => _reassembler.Push(segment.SequenceNumber, segment.Payload, onDataReady);

    public void Dispose() => _reassembler.Dispose();
}
