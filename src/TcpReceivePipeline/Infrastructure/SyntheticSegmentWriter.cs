using System.Buffers.Binary;
using TcpReceivePipeline.Core;

namespace TcpReceivePipeline.Infrastructure;

public static class SyntheticSegmentWriter
{
    public static int Write(
        Span<byte> destination, ushort sourcePort, ushort destPort,
        uint sequenceNumber, uint ackNumber, TcpFlags flags, ushort window,
        ReadOnlySpan<byte> payload)
    {
        int total = TcpHeaderParser.MinHeaderLength + payload.Length;
        if (destination.Length < total)
            throw new ArgumentException("Destination buffer too small.", nameof(destination));

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), destPort);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), ackNumber);

        destination[12] = 5 << 4; // data offset = 5 words, no options
        destination[13] = (byte)flags;

        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(14, 2), window);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(16, 2), 0); // checksum: not computed
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(18, 2), 0); // urgent pointer: unused

        payload.CopyTo(destination.Slice(TcpHeaderParser.MinHeaderLength));
        return total;
    }
}
