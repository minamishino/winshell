namespace WinShell.Infrastructure.Windows.Terminal;

// One producer and one reader; all sixteen buffers include filling, queued and partially read blocks.
internal sealed class BoundedOutputStream : Stream
{
    internal const int BlockSize = 64 * 1024;
    internal const int BlockCount = 16;
    private readonly Lock _gate = new();
    private readonly Queue<byte[]> _free = new(Enumerable.Range(0, BlockCount).Select(_ => new byte[BlockSize]));
    private readonly Queue<(byte[] Bytes, int Count)> _ready = new();
    private TaskCompletionSource _changed = NewSignal();
    private (byte[] Bytes, int Count)? _reading;
    private int _offset;
    private int _readerActive;
    private bool _complete;
    private bool _producerStarted;
    private bool _buffersReleased;
    private bool _discard;
    private Exception? _error;
    public int MaximumOwnedBlocks { get; private set; }
    public long DiscardedBytes { get; private set; }
    public int OwnedBlocks { get { lock (_gate) return _buffersReleased ? 0 : BlockCount - _free.Count; } }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal() { var signal = _changed; _changed = NewSignal(); signal.TrySetResult(); }

    public async Task PumpAsync(Stream source, int maximumRead = BlockSize)
    {
        lock (_gate)
        {
            if (_producerStarted || _buffersReleased) throw new InvalidOperationException("Output producer cannot be started twice or after buffer release.");
            _producerStarted = true;
        }
        byte[]? filling = null;
        try
        {
            while (true)
            {
                while (filling is null)
                {
                    Task available;
                    lock (_gate)
                    {
                        if (_free.TryDequeue(out filling))
                        {
                            MaximumOwnedBlocks = Math.Max(MaximumOwnedBlocks, BlockCount - _free.Count);
                            break;
                        }
                        available = _changed.Task;
                    }
                    await available.ConfigureAwait(false);
                }
                var count = await source.ReadAsync(filling.AsMemory(0, Math.Min(BlockSize, maximumRead))).ConfigureAwait(false);
                lock (_gate)
                {
                    if (count == 0 || _discard)
                    {
                        DiscardedBytes += count;
                        _free.Enqueue(filling);
                    }
                    else _ready.Enqueue((filling, count));
                    filling = null;
                    Signal();
                }
                if (count == 0) break;
            }
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) == 109) { } // Pipe EOF from ConPTY.
        catch (Exception exception)
        {
            lock (_gate) _error = exception;
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (filling is not null) _free.Enqueue(filling);
                _complete = true;
                Signal();
            }
        }
    }

    // Explicit close abandons presentation, wakes a producer waiting for capacity and drains its pipe.
    public void DiscardRemaining()
    {
        lock (_gate)
        {
            _discard = true;
            while (_ready.TryDequeue(out var block)) { DiscardedBytes += block.Count; _free.Enqueue(block.Bytes); }
            if (_reading is { } current)
            {
                DiscardedBytes += current.Count - _offset;
                _free.Enqueue(current.Bytes);
                _reading = null;
                _offset = 0;
            }
            Signal();
        }
    }

    public void ReleaseBuffersAfterProducer()
    {
        lock (_gate)
        {
            if (_producerStarted && !_complete) throw new InvalidOperationException("Join the output producer before releasing buffers.");
            _discard = true;
            _buffersReleased = true;
            _free.Clear();
            _ready.Clear();
            _reading = null;
            Signal();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (Interlocked.Exchange(ref _readerActive, 1) != 0) throw new InvalidOperationException("Only one output reader is supported.");
        try
        {
            while (true)
            {
                Task available;
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_reading is null && _ready.TryDequeue(out var next)) _reading = next;
                    if (_reading is { } block)
                    {
                        var count = Math.Min(buffer.Length, block.Count - _offset);
                        block.Bytes.AsMemory(_offset, count).CopyTo(buffer);
                        _offset += count;
                        if (_offset != block.Count) return count;
                        _free.Enqueue(block.Bytes);
                        _reading = null;
                        _offset = 0;
                        Signal();
                        return count;
                    }
                    if (_discard) return 0;
                    if (_complete)
                    {
                        if (_error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_error).Throw();
                        return 0;
                    }
                    available = _changed.Task;
                }
                await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _readerActive, 0); }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous output reads.");
    // The destination retains the existing pool block until its actual renderer ACK.
    // A local lease is never returned by DiscardRemaining while the destination still uses it.
    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _readerActive, 1) != 0) throw new InvalidOperationException("Only one output reader is supported.");
        try
        {
            while (true)
            {
                (byte[] Bytes, int Count)? borrowed = null;
                Task available;
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_reading is not null) throw new InvalidOperationException("CopyTo must own the stream from its first read.");
                    if (_ready.TryDequeue(out var next)) borrowed = next;
                    else if (_discard) return;
                    else if (_complete)
                    {
                        if (_error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_error).Throw();
                        return;
                    }
                    available = _changed.Task;
                }
                if (borrowed is { } block)
                {
                    try { await destination.WriteAsync(block.Bytes.AsMemory(0, block.Count), cancellationToken).ConfigureAwait(false); }
                    finally { lock (_gate) { if (!_buffersReleased) _free.Enqueue(block.Bytes); Signal(); } }
                }
                else await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _readerActive, 0); }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
