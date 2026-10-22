namespace WinShell.Infrastructure.Ssh;

// At most four borrowed bridge blocks, including the partially consumed block.
// No copies or pending admission queue. Completion releases the caller's memory
// only after the existing Shell worker copied it into its fixed 16KiB scratch.
internal sealed class BorrowedInputStream : Stream
{
    private sealed class Block(ReadOnlyMemory<byte> bytes)
    {
        internal ReadOnlyMemory<byte> Bytes = bytes;
        internal CancellationTokenRegistration Cancellation;
        internal readonly TaskCompletionSource Copied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Lock _gate = new();
    private readonly List<Block> _blocks = new(4);
    private TaskCompletionSource _changed = NewSignal();
    private bool _closed;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > 65536) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes.IsEmpty) return ValueTask.CompletedTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_blocks.Count == 4) throw new InvalidOperationException("Input admission is full (four borrowed blocks).");
            var block = new Block(bytes); _blocks.Add(block);
            block.Cancellation = cancellationToken.Register(() =>
            {
                lock (_gate)
                {
                    if (!_blocks.Remove(block)) return;
                    block.Bytes = default;
                    block.Copied.TrySetCanceled(cancellationToken);
                }
            });
            var changed = _changed; _changed = NewSignal(); changed.TrySetResult();
            return new ValueTask(block.Copied.Task);
        }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_blocks.Count > 0)
                {
                    var block = _blocks[0];
                    var count = Math.Min(buffer.Length, block.Bytes.Length);
                    block.Bytes[..count].CopyTo(buffer); block.Bytes = block.Bytes[count..];
                    if (!block.Bytes.IsEmpty) return count;
                    _blocks.RemoveAt(0); block.Cancellation.Unregister(); block.Copied.TrySetResult();
                    return count;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_gate)
        {
            _closed = true;
            foreach (var block in _blocks) { block.Cancellation.Unregister(); block.Bytes = default; block.Copied.TrySetCanceled(); }
            _blocks.Clear();
            _changed.TrySetResult();
        }
        base.Dispose(disposing);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
