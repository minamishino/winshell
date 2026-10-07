using System.Buffers.Binary;
using WinShell.Terminal.Shared;

namespace WinShell.Infrastructure.Windows.Terminal;

public sealed class RendererClient(RendererJob job, string executable) : IAsyncDisposable
{
    private RendererProcessOwner? _owner;
    private RendererFrameCodec? _writer, _reader;
    private readonly SemaphoreSlim _serial = new(1);
    private readonly SemaphoreSlim _writeSerial = new(1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _read, _close, _start, _monitor;
    private readonly List<Task> _inputs = [];
    private readonly List<Task<RendererFrame>> _requests = [];
    private readonly TaskCompletionSource<Exception> _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;
    public Task<Exception> FailureCompletion => _failed.Task;
    public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? InputConsumer { get; set; }
    public ulong PeakPrivateBytes { get; private set; }
    private TaskCompletionSource<RendererFrame>? _reply;
    public nint ChildWindow { get; private set; }
    public int? ProcessId => _owner?.ProcessId;
    public int? ExitCode { get; private set; }
    public Exception? Failure { get; private set; }
    public bool WasTerminated => _owner?.WasTerminated ?? false;
    public event EventHandler<ReadOnlyMemory<byte>>? InputReceived;
    public event EventHandler<(int Top, int Height, int Total)>? ScrollChanged;
    public async Task ScrollAsync(int top, CancellationToken token) => await RequestAsync(RendererFrameType.Scroll, BitConverter.GetBytes(top), token).ConfigureAwait(false);
    public async Task<string> CopySelectionAsync(CancellationToken token)
    {
        var result = await RequestAsync(RendererFrameType.Copy, default, token).ConfigureAwait(false);
        if (result.Type != RendererFrameType.Selection) throw new InvalidDataException("Expected bounded selection.");
        return new System.Text.UTF8Encoding(false, true).GetString(result.Payload.Span);
    }
    public async Task SetThemeAsync(bool dark, int dpi, CancellationToken token)
    {
        var bytes = new byte[5]; bytes[0] = dark ? (byte)1 : (byte)0; BitConverter.TryWriteBytes(bytes.AsSpan(1), dpi);
        await RequestAsync(RendererFrameType.Theme, bytes, token).ConfigureAwait(false);
    }
    public async Task SetThemeAsync(bool dark, int dpi, uint backgroundColor, uint foregroundColor, CancellationToken token)
    {
        if (dpi is < 96 or > 960) throw new ArgumentOutOfRangeException(nameof(dpi));
        if (backgroundColor > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(backgroundColor));
        if (foregroundColor > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(foregroundColor));
        var bytes = new byte[13];
        bytes[0] = dark ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), dpi);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), backgroundColor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9), foregroundColor);
        await RequestAsync(RendererFrameType.Theme, bytes, token).ConfigureAwait(false);
    }
    public async Task CompleteOutputAsync(CancellationToken cancellationToken) => await RequestAsync(RendererFrameType.CompleteOutput, default, cancellationToken).ConfigureAwait(false);
    public async Task<(ushort Columns, ushort Rows)> ResizeAsync(int width, int height, CancellationToken cancellationToken)
    {
        var bytes = new byte[8]; BitConverter.TryWriteBytes(bytes.AsSpan(), width); BitConverter.TryWriteBytes(bytes.AsSpan(4), height);
        var result = await RequestAsync(RendererFrameType.Resize, bytes, cancellationToken).ConfigureAwait(false);
        if (result.Type != RendererFrameType.Size || result.Payload.Length != 8) throw new InvalidDataException("Expected actual native size.");
        var columns = BitConverter.ToInt32(result.Payload.Span); var rows = BitConverter.ToInt32(result.Payload.Span[4..]);
        if (columns is < 1 or > 400 || rows is < 1 or > 160) throw new InvalidDataException("Viewport outside budget.");
        return (checked((ushort)columns), checked((ushort)rows));
    }
    public async Task SetFocusedAsync(bool focused, CancellationToken token) => await RequestAsync(RendererFrameType.Focus, new byte[] { focused ? (byte)1 : (byte)0 }, token).ConfigureAwait(false);
    public async Task SetVisibleAsync(bool visible, CancellationToken token) => await RequestAsync(RendererFrameType.Visibility, new byte[] { visible ? (byte)1 : (byte)0 }, token).ConfigureAwait(false);
    public async Task StartAsync(nint parentWindow, CancellationToken cancellationToken)
    {
        Task start;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_start is not null) throw new InvalidOperationException("A renderer instance can only start once.");
            start = _start = StartCoreAsync(parentWindow, cancellationToken);
        }
        try { await start.ConfigureAwait(false); }
        catch (Exception error)
        {
            Fail(error);
            try { await CloseAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }
    private async Task StartCoreAsync(nint parentWindow, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            _owner = await RendererProcessOwner.StartAsync(job, executable, ["--renderer"], deadline.Token).ConfigureAwait(false);
            _writer = new(_owner.Input); _reader = new(_owner.Output);
            _read = Task.Run(ReadAsync);
            _monitor = MonitorAsync();
            var reply = await RequestAsync(RendererFrameType.Initialize, BitConverter.GetBytes(parentWindow.ToInt64()), deadline.Token).ConfigureAwait(false);
            if (reply.Type != RendererFrameType.Ready || reply.Payload.Length != 8) throw new InvalidDataException("Expected renderer HWND.");
            ChildWindow = (nint)BitConverter.ToInt64(reply.Payload.Span);
        }
        catch (Exception error)
        {
            Fail(error);
            throw;
        }
    }
    public async Task ConsumeOutputAsync(ReadOnlyMemory<byte> output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await RequestAsync(RendererFrameType.Output, output, deadline.Token).ConfigureAwait(false);
        if (result.Type != RendererFrameType.Ack) throw new InvalidDataException("Expected output ACK.");
    }
    private Task<RendererFrame> RequestAsync(RendererFrameType type, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _requests.RemoveAll(task => task.IsCompleted);
            // One output producer plus coalesced presentation/scroll and one user operation.
            if (_requests.Count >= 4) throw new InvalidOperationException("Renderer request admission is full.");
            var request = RequestCoreAsync(type, data, cancellationToken);
            _requests.Add(request);
            return request;
        }
    }
    private async Task<RendererFrame> RequestCoreAsync(RendererFrameType type, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var sequence = await WriteAsync(type, data, cancellationToken).ConfigureAwait(false);
            var response = await _reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (response.Type == RendererFrameType.Ack && (response.Payload.Length != 8 || BitConverter.ToUInt64(response.Payload.Span) != sequence)) throw new InvalidDataException("Renderer ACK sequence mismatch.");
            return response;
        }
        finally { _reply = null; _serial.Release(); }
    }
    private async Task ReadAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frame = await _reader!.ReadAsync(_stop.Token).ConfigureAwait(false);
                if (frame.Type == RendererFrameType.Input)
                {
                    lock (_gate)
                    {
                        _inputs.RemoveAll(task => task.IsCompletedSuccessfully);
                        if (_inputs.Count >= 4) throw new InvalidDataException("Renderer input budget exceeded.");
                        _inputs.Add(ConsumeInputAsync(frame.Payload));
                    }
                }
                else if (frame.Type == RendererFrameType.ScrollState)
                {
                    if (frame.Payload.Length != 12) throw new InvalidDataException("Invalid scroll state.");
                    ScrollChanged?.Invoke(this, (BitConverter.ToInt32(frame.Payload.Span), BitConverter.ToInt32(frame.Payload.Span[4..]), BitConverter.ToInt32(frame.Payload.Span[8..])));
                }
                else if (_reply is { } reply) reply.TrySetResult(frame);
                else throw new InvalidDataException("Unexpected renderer reply.");
            }
        }
        catch (Exception error)
        {
            if (!_stop.IsCancellationRequested) Fail(error);
            _reply?.TrySetException(error);
        }
    }
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_close is not null) return _close;
            cancellationToken.ThrowIfCancellationRequested();
            _closing = true;
            _stop.Cancel();
            _reply?.TrySetCanceled();
            return _close = Task.Run(CloseCoreAsync);
        }
    }
    private async Task CloseCoreAsync()
    {
        _stop.Cancel();
        _reply?.TrySetCanceled();
        if (_start is not null) { try { await _start.ConfigureAwait(false); } catch (Exception error) { Fail(error); } }
        if (_owner is null) { _serial.Dispose(); _writeSerial.Dispose(); _stop.Dispose(); return; }
        try
        {
            if (!_owner.Completion.IsCompleted)
            {
                using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _serial.WaitAsync(graceful.Token).ConfigureAwait(false);
                    try { await WriteAsync(RendererFrameType.Close, default, graceful.Token).ConfigureAwait(false); }
                    finally { _serial.Release(); }
                    await _owner.Completion.WaitAsync(graceful.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException) { _owner.Terminate(); }
            }
            ExitCode = await _owner.Completion.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (_read is not null) await _read.ConfigureAwait(false);
                if (_monitor is not null) await _monitor.ConfigureAwait(false);
                Task[] inputs;
                lock (_gate) inputs = _inputs.ToArray();
                await Task.WhenAll(inputs).ConfigureAwait(false);
                Task<RendererFrame>[] requests;
                lock (_gate) requests = _requests.ToArray();
                foreach (var request in requests)
                {
                    try { await request.ConfigureAwait(false); }
                    catch (Exception error) { if (error is not OperationCanceledException) Fail(error); }
                }
            }
            finally
            {
                await _owner.DisposeAsync().ConfigureAwait(false);
                _serial.Dispose(); _writeSerial.Dispose(); _stop.Dispose();
            }
        }
    }
    private async Task<ulong> WriteAsync(RendererFrameType type, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _writeSerial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _writer!.WriteAsync(type, data, cancellationToken).ConfigureAwait(false); }
        finally { _writeSerial.Release(); }
    }
    private async Task ConsumeInputAsync(ReadOnlyMemory<byte> data)
    {
        try
        {
            InputReceived?.Invoke(this, data);
            if (InputConsumer is { } consume) await consume(data, _stop.Token).ConfigureAwait(false);
            await WriteAsync(RendererFrameType.InputAck, default, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) { Fail(error); }
    }
    private async Task MonitorAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_owner!.Completion.IsCompleted)
                {
                    var code = await _owner.Completion.ConfigureAwait(false);
                    if (!_closing) Fail(new IOException($"原生终端进程意外退出，代码 0x{code:X8}。"));
                    return;
                }
                var bytes = _owner.PrivateBytes;
                PeakPrivateBytes = Math.Max(PeakPrivateBytes, bytes);
                if (bytes >= 112UL * 1024 * 1024) { Fail(new IOException("原生终端已达到内存预算。")); return; }
                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { if (!_closing) Fail(error); }
    }
    private void Fail(Exception error)
    {
        lock (_gate) { Failure ??= error; _failed.TrySetResult(Failure); }
    }
    public ValueTask DisposeAsync() => new(CloseAsync(CancellationToken.None));
}
