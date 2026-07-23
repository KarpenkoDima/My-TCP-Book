namespace TcpSlidingWindow.Core;

/// <summary>
/// A parsed view over a single TCP segment (RFC 9293 §3.1).
/// This is a `readonly ref struct` on purpose: it carries a
/// <see cref="ReadOnlySpan{T}"/> over the caller's buffer, so it can only
/// live on the stack and can never be boxed, captured in a closure, or
/// stored in a field of a heap object. That is exactly the lifetime we
/// want for a per-packet, zero-allocation parse result — the segment is
/// read once and its data is projected into the sliding-window tracker's
/// own (value-type) state before the buffer is reused or returned to a
/// pool.
/// </summary>
public readonly ref struct TcpSegment
{
    public ushort SourcePort { get; }
    public ushort DestinationPort { get; }

    /// <summary>SND/RCV sequence number of the first payload byte (RFC 9293 §3.3.1).</summary>
    public uint SequenceNumber { get; }

    /// <summary>Valid only when <see cref="Flags"/> has the ACK bit set.</summary>
    public uint AcknowledgmentNumber { get; }

    /// <summary>Header length in 32-bit words (top nibble of byte 12). 5..15.</summary>
    public byte DataOffsetWords { get; }

    public TcpFlags Flags { get; }

    /// <summary>Receiver's advertised window, in bytes (RFC 9293 §3.3.1). Not yet scaled.</summary>
    public ushort WindowSize { get; }

    public ushort Checksum { get; }
    public ushort UrgentPointer { get; }

    /// <summary>Segment payload, i.e. everything after the (options-inclusive) header.</summary>
    public ReadOnlySpan<byte> Payload { get; }

    public int HeaderLength => DataOffsetWords * 4;

    public TcpSegment(
        ushort sourcePort,
        ushort destinationPort,
        uint sequenceNumber,
        uint acknowledgmentNumber,
        byte dataOffsetWords,
        TcpFlags flags,
        ushort windowSize,
        ushort checksum,
        ushort urgentPointer,
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
