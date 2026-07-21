using System.Buffers.Binary;
using TcpSlidingWindow.Core;

namespace TcpSlidingWindow.Infrastructure;

/// <summary>
/// Parses a TCP segment straight out of the wire buffer with zero heap
/// allocations: all multi-byte fields are read big-endian (network byte
/// order, RFC 9293 §3.1) via <see cref="BinaryPrimitives"/>, and the
/// payload is exposed as a slice of the caller's own buffer rather than
/// copied. This mirrors the project's packet-capture path, where the
/// buffer typically comes from a pinned/pooled array and must never be
/// re-allocated per packet.
/// </summary>
public static class TcpHeaderParser
{
    public const int MinHeaderLength = 20;

    public static bool TryParse(ReadOnlySpan<byte> buffer, out TcpSegment segment)
    {
        segment = default;

        if (buffer.Length < MinHeaderLength)
            return false;

        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(0, 2));
        ushort destPort = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        uint sequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        uint ackNumber = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4));

        byte dataOffsetByte = buffer[12];
        byte dataOffsetWords = (byte)(dataOffsetByte >> 4);
        int headerLength = dataOffsetWords * 4;

        if (headerLength < MinHeaderLength || buffer.Length < headerLength)
            return false; // malformed or truncated capture

        var flags = (TcpFlags)buffer[13];
        ushort window = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(14, 2));
        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(16, 2));
        ushort urgentPointer = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(18, 2));

        ReadOnlySpan<byte> payload = buffer[headerLength..];

        segment = new TcpSegment(
            sourcePort, destPort, sequenceNumber, ackNumber,
            dataOffsetWords, flags, window, checksum, urgentPointer, payload);
        return true;
    }
}
