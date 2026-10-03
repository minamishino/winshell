using System.Buffers;

namespace WinShell.Mcp.Transport;

/// <summary>Hard-cap staging; the serializer cannot grow the backing array through sizeHint.</summary>
internal sealed class CappedBufferWriter(int capacity) : IBufferWriter<byte>
{
    private readonly byte[] _buffer = new byte[capacity];
    private int _count;
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _count);
    public void Advance(int count)
    {
        if (count < 0 || count > _buffer.Length - _count) throw new InvalidDataException("Output limit.");
        _count += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint < 0 || Math.Max(1, sizeHint) > _buffer.Length - _count) throw new InvalidDataException("Output limit.");
        return _buffer.AsMemory(_count);
    }
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
}
