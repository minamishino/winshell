using System.Buffers;

namespace WinShell.Mcp.Http;

internal sealed class HttpCappedBuffer(int capacity) : IBufferWriter<byte>
{
    private byte[] _buffer = new byte[Math.Min(256, capacity)];
    private int _count;
    public void Advance(int count)
    {
        if (count < 0 || count > _buffer.Length - _count) throw new InvalidDataException("message_limit");
        _count += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint < 0 || Math.Max(1, sizeHint) > capacity - _count) throw new InvalidDataException("message_limit");
        var required = _count + Math.Max(1, sizeHint);
        if (required <= _buffer.Length) return _buffer.AsMemory(_count);
        var length = _buffer.Length;
        while (length < required) length = (int)Math.Min(capacity, (long)length * 2);
        Array.Resize(ref _buffer, length);
        return _buffer.AsMemory(_count);
    }
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
}

/// <summary>The SDK owns serialization/framing. This wrapper only caps physical response bytes.</summary>
internal sealed class HttpBoundedOutput(Stream output, long maximumBytes, Action stop) : Stream
{
    private long _written;
    private void Reserve(int count)
    {
        if (Interlocked.Add(ref _written, count) <= maximumBytes) return;
        stop();
        throw new IOException("stream_limit");
    }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => output.Flush();
    public override Task FlushAsync(CancellationToken token) => output.FlushAsync(token);
    public override void Write(byte[] buffer, int offset, int count) { Reserve(count); output.Write(buffer, offset, count); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    { Reserve(buffer.Length); await output.WriteAsync(buffer, token).ConfigureAwait(false); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
