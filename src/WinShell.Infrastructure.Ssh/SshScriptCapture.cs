using System.Security.Cryptography;
using System.Text;
using WinShell.Core.Ssh;

namespace WinShell.Infrastructure.Ssh;

/// <summary>Fixed raw-byte storage; decoded only after the SSH listener has joined.</summary>
internal sealed class SshScriptCapture(int capacity) : Stream
{
    private readonly byte[] _bytes = new byte[capacity];
    private readonly Lock _gate = new();
    private int _count;
    private bool _disposed;
    internal SshConnectionException? Failure { get; private set; }
    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Failure is not null) throw Failure;
            if (count > _bytes.Length - _count)
                throw Failure = new SshConnectionException(SshFailureKind.Capacity, "SSH 脚本输出超过字节上限。");
            buffer.AsSpan(offset, count).CopyTo(_bytes.AsSpan(_count));
            _count += count;
        }
    }
    internal string Decode()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Failure is not null) throw Failure;
            try { return new UTF8Encoding(false, true).GetString(_bytes, 0, _count); }
            catch (DecoderFallbackException error)
            { throw Failure = new SshConnectionException(SshFailureKind.Protocol, "SSH 脚本输出不是有效 UTF-8。", error); }
        }
    }
    protected override void Dispose(bool disposing)
    {
        lock (_gate) { _disposed = true; CryptographicOperations.ZeroMemory(_bytes); }
        base.Dispose(disposing);
    }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
