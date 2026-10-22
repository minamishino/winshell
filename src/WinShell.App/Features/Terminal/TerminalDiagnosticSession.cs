using System.Text;
using WinShell.Core.Terminal;

namespace WinShell.App.Features.Terminal;

// The last 16 KiB is a ended-session diagnostic, never an interactive terminal surface.
// Delegate CopyTo to the source so its borrowed block stays owned through the renderer ACK.
internal sealed partial class TerminalDiagnosticSession(IShellSession inner) : IShellSession
{
    private readonly DiagnosticOutput _output = new(inner.Output);
    internal IShellSessionExit? Result => inner as IShellSessionExit;
    internal string Snapshot() => _output.Snapshot();
    public Stream Output => _output;
    public Task OutputCompletion => inner.OutputCompletion;
    public Task StartAsync(CancellationToken token) => inner.StartAsync(token);
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => inner.WriteInputAsync(bytes, token);
    public ValueTask ResizeAsync(ushort columns, ushort rows, CancellationToken token) => inner.ResizeAsync(columns, rows, token);
    public ValueTask CloseAsync(CancellationToken token) => inner.CloseAsync(token);
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private sealed partial class DiagnosticOutput(Stream source) : Stream
    {
        private const int Capacity = 16 * 1024;
        private readonly byte[] _tail = new byte[Capacity];
        private int _next, _count;
        private bool _truncated;
        private void Append(ReadOnlySpan<byte> bytes)
        {
            lock (_tail)
            {
                _truncated |= bytes.Length > Capacity - _count;
                if (bytes.Length >= Capacity) { bytes[^Capacity..].CopyTo(_tail); _next = 0; _count = Capacity; return; }
                var first = Math.Min(bytes.Length, Capacity - _next);
                bytes[..first].CopyTo(_tail.AsSpan(_next));
                bytes[first..].CopyTo(_tail);
                _next = (_next + bytes.Length) % Capacity;
                _count = Math.Min(Capacity, _count + bytes.Length);
            }
        }
        internal string Snapshot()
        {
            lock (_tail)
            {
                var bytes = new byte[_count];
                var start = (_next - _count + Capacity) % Capacity;
                var first = Math.Min(_count, Capacity - start);
                _tail.AsSpan(start, first).CopyTo(bytes);
                _tail.AsSpan(0, _count - first).CopyTo(bytes.AsSpan(first));
                return (_truncated ? "仅保留最后 16 KiB 输出。\n" : "") + Encoding.UTF8.GetString(bytes);
            }
        }
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token) => source.CopyToAsync(new CaptureDestination(this, destination), bufferSize, token);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var count = await source.ReadAsync(buffer, token).ConfigureAwait(false); Append(buffer.Span[..count]); return count; }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = source.Read(buffer, offset, count); Append(buffer.AsSpan(offset, read)); return read; }
        private sealed partial class CaptureDestination(DiagnosticOutput owner, Stream destination) : Stream
        {
            public override Task WriteAsync(byte[] bytes, int offset, int count, CancellationToken token) => WriteAsync(bytes.AsMemory(offset, count), token).AsTask();
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
            { owner.Append(bytes.Span); await destination.WriteAsync(bytes, token).ConfigureAwait(false); }
            public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
