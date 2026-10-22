using System.Buffers.Binary;

namespace WinShell.Terminal.Shared;

internal sealed class RendererFrameCodec(Stream stream)
{
    internal const int MaximumPayload = 65536;
    private const RendererFrameType LastType = RendererFrameType.Theme;
    private ulong _readSequence, _writeSequence;
    public async Task<RendererFrame> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var type = (RendererFrameType)BinaryPrimitives.ReadUInt16LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        if (type is < RendererFrameType.Initialize or > LastType || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) != 0 ||
            length is < 0 or > MaximumPayload || _readSequence == ulong.MaxValue || sequence != _readSequence + 1)
            throw new InvalidDataException("Invalid renderer frame header.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        var frame = new RendererFrame(type, sequence, payload);
        _readSequence = frame.Sequence;
        return frame;
    }
    // Each direction has a single writer; callers serialize before entering, with bounded admission.
    public async Task<ulong> WriteAsync(RendererFrameType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (type is < RendererFrameType.Initialize or > LastType || payload.Length > MaximumPayload) throw new InvalidDataException("Invalid renderer frame.");
        var header = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)type);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), checked(++_writeSequence));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        return _writeSequence;
    }
}
