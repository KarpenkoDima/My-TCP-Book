using System.Buffers.Binary;
using TcpReceivePipeline.Core;

namespace TcpReceivePipeline.Infrastructure;

public static class TcpHeaderParser
{
    public const int MinHeaderLength = 20;

    public static bool TryParse(ReadOnlySpan<byte> buffer, out TcpSegment segment)
    {
        segment = default;
        if (buffer.Length < MinHeaderLength) return false;

        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(0, 2));
        ushort destPort = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        uint sequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        uint ackNumber = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4));

        byte dataOffsetByte = buffer[12];
        byte dataOffsetWords = (byte)(dataOffsetByte >> 4);
        int headerLength = dataOffsetWords * 4;

        if (headerLength < MinHeaderLength || buffer.Length < headerLength)
            return false;

        var flags = (TcpFlags)buffer[13];
        ushort window = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(14, 2));
        ushort checksum = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(16, 2));
        ushort urgentPointer = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(18, 2));

        ReadOnlySpan<byte> payload = buffer[headerLength..];

        segment = new TcpSegment(sourcePort, destPort, sequenceNumber, ackNumber,
            dataOffsetWords, flags, window, checksum, urgentPointer, payload);
        return true;
    }
}
