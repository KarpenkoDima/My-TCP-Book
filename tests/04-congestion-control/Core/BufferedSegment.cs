using System.Buffers;

namespace TcpReceivePipeline.Core;

internal sealed class BufferedSegment : IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private byte[]? _buffer;

    public BufferedSegment(long start, ReadOnlySpan<byte> data, ArrayPool<byte> pool)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (data.IsEmpty)
            throw new ArgumentException("Buffered segment cannot be empty.", nameof(data));

        _pool = pool;
        _buffer = pool.Rent(data.Length);
        data.CopyTo(_buffer);
        Start = start;
        Length = data.Length;
    }

    public long Start { get; }
    public int Length { get; }
    public long End => checked(Start + Length);

    public ReadOnlySpan<byte> Data
    {
        get
        {
            byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(BufferedSegment));
            return buffer.AsSpan(0, Length);
        }
    }

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            _pool.Return(buffer, clearArray: false);
    }
}
