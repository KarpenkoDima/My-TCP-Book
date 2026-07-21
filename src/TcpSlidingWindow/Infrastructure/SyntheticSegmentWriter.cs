using System.Buffers.Binary;
using TcpSlidingWindow.Core;

namespace TcpSlidingWindow.Infrastructure;

/// <summary>
/// Builds a minimal (no-options) TCP segment directly into a caller-owned
/// buffer. This exists only to produce realistic wire bytes for the demo —
/// in production this buffer would come from a NIC capture, not this
/// writer — but it uses the same zero-allocation discipline: no
/// intermediate arrays, everything written via BinaryPrimitives.
/// </summary>
public static class SyntheticSegmentWriter
{
    /// <summary>Returns the number of bytes written (20-byte header + payload).</summary>
    public static int Write(
        Span<byte> destination,
        ushort sourcePort,
        ushort destPort,
        uint sequenceNumber,
        uint ackNumber,
        TcpFlags flags,
        ushort window,
        ReadOnlySpan<byte> payload)
    {
        int total = TcpHeaderParser.MinHeaderLength + payload.Length;
        if (destination.Length < total)
            throw new ArgumentException("Destination buffer too small.", nameof(destination));

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), destPort);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), ackNumber);

        // Data offset = 5 words (20 bytes), no options, top nibble of byte 12.
        destination[12] = 5 << 4;
        destination[13] = (byte)flags;

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(14, 2), window);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(16, 2), 0); // checksum: not computed for this demo
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(18, 2), 0); // urgent pointer: unused

        payload.CopyTo(destination.Slice(TcpHeaderParser.MinHeaderLength));
        return total;
    }
}
