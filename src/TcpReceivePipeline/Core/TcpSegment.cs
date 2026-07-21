namespace TcpReceivePipeline.Core;

/// <summary>
/// Zero-allocation parsed view over one TCP segment. `readonly ref struct`
/// on purpose: carries a Span over the caller's buffer, so it can only
/// live on the stack for the duration of one parse — exactly the lifetime
/// a per-packet result should have.
/// </summary>
public readonly ref struct TcpSegment
{
    public ushort SourcePort { get; }
    public ushort DestinationPort { get; }
    public uint SequenceNumber { get; }
    public uint AcknowledgmentNumber { get; }
    public byte DataOffsetWords { get; }
    public TcpFlags Flags { get; }
    public ushort WindowSize { get; }
    public ushort Checksum { get; }
    public ushort UrgentPointer { get; }
    public ReadOnlySpan<byte> Payload { get; }

    public int HeaderLength => DataOffsetWords * 4;

    public TcpSegment(
        ushort sourcePort, ushort destinationPort, uint sequenceNumber, uint acknowledgmentNumber,
        byte dataOffsetWords, TcpFlags flags, ushort windowSize, ushort checksum, ushort urgentPointer,
        ReadOnlySpan<byte> payload)
    {
        SourcePort = sourcePort;
        DestinationPort = destinationPort;
        SequenceNumber = sequenceNumber;
        AcknowledgmentNumber = acknowledgmentNumber;
        DataOffsetWords = dataOffsetWords;
        Flags = flags;
        WindowSize = windowSize;
        Checksum = checksum;
        UrgentPointer = urgentPointer;
        Payload = payload;
    }

    public bool Has(TcpFlags flag) => (Flags & flag) == flag;
}
