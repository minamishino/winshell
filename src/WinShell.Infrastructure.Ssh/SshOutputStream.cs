using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace WinShell.Infrastructure.Ssh;

// SSH's listener writes synchronously. All filling/queued/partly read blocks belong
// to this single fixed pool; a full pool backpressures that listener before return.
internal sealed class SshOutputStream : Stream
{
    private const int BlockSize = 65536, BlockCount = 16;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _free = new(Enumerable.Range(0, BlockCount).Select(_ => new byte[BlockSize]));
    private readonly Queue<(byte[] Bytes, int Count)> _ready = new();
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private TaskCompletionSource _changed = NewSignal();
    private (byte[] Bytes, int Count)? _reading;
    private int _offset, _readerActive;
    private bool _stopped, _complete, _disposed, _discard;
    private Exception? _failure;
    private long _bytesRead, _discardedBytes;
    public int MaximumOwnedBlocks { get; private set; }
    public long BytesRead { get { lock (_gate) return _bytesRead; } }
    public long DiscardedBytes { get { lock (_gate) return _discardedBytes; } }
    public string ReadHash { get { lock (_gate) return Convert.ToHexString(_hash.GetCurrentHash()); } }
    public TaskCompletionSource Backpressured { get; } = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal() { var changed = _changed; _changed = NewSignal(); changed.TrySetResult(); Monitor.PulseAll(_gate); }
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Invalid borrowed output range.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (count > 0)
            {
                while (_free.Count == 0 && !_stopped) { Backpressured.TrySetResult(); Monitor.Wait(_gate); }
                if (_stopped) { _discardedBytes += count; return; }
                var block = _free.Dequeue();
                MaximumOwnedBlocks = Math.Max(MaximumOwnedBlocks, BlockCount - _free.Count);
                var length = Math.Min(count, BlockSize);
                buffer.AsSpan(offset, length).CopyTo(block);
                _ready.Enqueue((block, length));
                offset += length; count -= length;
                Signal();
            }
        }
    }
    public void StopWriting(bool discard)
    {
        lock (_gate)
        {
            _stopped = true;
            if (discard)
            {
                _discard = true;
                while (_ready.TryDequeue(out var block)) { _discardedBytes += block.Count; _free.Enqueue(block.Bytes); }
                if (_reading is { } current) { _discardedBytes += current.Count - _offset; _free.Enqueue(current.Bytes); _reading = null; _offset = 0; }
            }
            Signal();
        }
    }
    public void Complete(Exception? failure = null) { lock (_gate) { _failure ??= failure; _complete = true; Signal(); } }
    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _readerActive, 1) != 0) throw new InvalidOperationException("Only one reader is admitted.");
        try
        {
            while (true)
            {
                (byte[] Bytes, int Count)? borrowed = null;
                Task changed;
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_reading is not null) throw new InvalidOperationException("CopyTo must own output from its first read.");
                    if (_ready.TryDequeue(out var next)) borrowed = next;
                    else if (_discard) return;
                    else if (_complete) { if (_failure is not null) ExceptionDispatchInfo.Capture(_failure).Throw(); return; }
                    changed = _changed.Task;
                }
                if (borrowed is { } block)
                {
                    try
                    {
                        await destination.WriteAsync(block.Bytes.AsMemory(0, block.Count), cancellationToken).ConfigureAwait(false);
                        lock (_gate) { if (!_disposed) { _hash.AppendData(block.Bytes.AsSpan(0, block.Count)); _bytesRead += block.Count; } }
                    }
                    finally { lock (_gate) { if (!_disposed) _free.Enqueue(block.Bytes); Signal(); } }
                }
                else await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _readerActive, 0); }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (Interlocked.Exchange(ref _readerActive, 1) != 0) throw new InvalidOperationException("Only one reader is admitted.");
        try
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_reading is null && _ready.TryDequeue(out var next)) _reading = next;
                    if (_reading is { } current)
                    {
                        var count = Math.Min(buffer.Length, current.Count - _offset);
                        var data = current.Bytes.AsMemory(_offset, count);
                        data.CopyTo(buffer); _hash.AppendData(data.Span); _bytesRead += count; _offset += count;
                        if (_offset != current.Count) return count;
                        _free.Enqueue(current.Bytes); _reading = null; _offset = 0; Signal();
                        return count;
                    }
                    if (_complete) { if (_failure is not null) ExceptionDispatchInfo.Capture(_failure).Throw(); return 0; }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _readerActive, 0); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_gate) { if (!_disposed) { _disposed = true; _hash.Dispose(); _free.Clear(); _ready.Clear(); _reading = null; Signal(); } }
        base.Dispose(disposing);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
