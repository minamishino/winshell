using System.Buffers;

namespace WinShell.Mcp.Transport;

/// <summary>Staging grows with actual demand; sizeHint cannot exceed the hard output cap.</summary>
internal sealed class CappedBufferWriter(int capacity) : IBufferWriter<byte>
{
    private byte[] _buffer = new byte[Math.Min(256, capacity)];
    private int _count;
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _count);
    public void Advance(int count)
    {
        if (count < 0 || count > _buffer.Length - _count) throw new InvalidDataException("Output limit.");
        _count += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint < 0 || Math.Max(1, sizeHint) > capacity - _count) throw new InvalidDataException("Output limit.");
        var required = _count + Math.Max(1, sizeHint);
        if (required > _buffer.Length)
        {
            var length = _buffer.Length;
            while (length < required) length = (int)Math.Min(capacity, (long)length * 2);
            Array.Resize(ref _buffer, length);
        }
        return _buffer.AsMemory(_count);
    }
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
}
