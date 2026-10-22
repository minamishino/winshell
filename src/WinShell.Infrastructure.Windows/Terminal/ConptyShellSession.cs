using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinShell.Infrastructure.Windows.Terminal;

public sealed class ConptyShellSession : WinShell.Core.Terminal.IShellSession, WinShell.Core.Terminal.IShellSessionExit
{
    private readonly string _executablePath;
    private readonly string[] _arguments;
    private readonly string? _workingDirectory;
    private readonly Lock _gate = new();
    private readonly BoundedOutputStream _output = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _inputSerial = new(1);
    private readonly HashSet<Task> _inputs = [];
    private AnonymousPipeServerStream? _inputPipe, _outputPipe;
    private ConptyNative.PseudoConsoleHandle? _console;
    private SafeProcessHandle? _process;
    private Task? _start, _close, _resize;
    private (ushort Columns, ushort Rows)? _pendingSize;
    private TaskCompletionSource _resizeRequested = NewSignal();
    private TaskCompletionSource? _resizeAcknowledged;
    private bool _closing, _running;
    private Exception? _inputFailure;
    public ConptyShellSession(string executablePath, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        _ = WindowsArguments.Build(executablePath, arguments);
        if (workingDirectory is not null && (!Path.IsPathFullyQualified(workingDirectory) || workingDirectory.Contains('\0'))) throw new ArgumentException("Working directory must be absolute and contain no NUL.", nameof(workingDirectory));
        _executablePath = executablePath;
        _arguments = [.. arguments];
        _workingDirectory = workingDirectory;
    }
    public Stream Output => _output;
    public Task OutputCompletion { get; private set; } = Task.CompletedTask;
    public Task ProcessCompletion { get; private set; } = Task.CompletedTask;
    public int? ExitCode { get; private set; }
    public int? ProcessId { get; private set; }
    public int NativeCloseCount { get; private set; }
    public bool ProcessTerminationRequested { get; private set; }
    public int OwnedHandleCount => (_inputPipe is not null ? 1 : 0) + (_outputPipe is not null ? 1 : 0) + (_console is { IsClosed: false } ? 1 : 0) + (_process is { IsClosed: false } ? 1 : 0);
    public int MaximumOutputBlocks => _output.MaximumOwnedBlocks;
    public int OutputBlocks => _output.OwnedBlocks;
    public int InputBlocks { get { lock (_gate) return _inputs.Count(task => !task.IsCompleted); } }
    public int PendingTaskCount
    {
        get { lock (_gate) return new[] { _start, _resize, OutputCompletion, ProcessCompletion }.Count(task => task is { IsCompleted: false }) + _inputs.Count(task => !task.IsCompleted); }
    }
    public long DiscardedOutputBytes => _output.DiscardedBytes;
    public Action<string>? AcquisitionObserved { get; init; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Task start;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_start is not null) throw new InvalidOperationException("Connection already started.");
            start = _start = Task.Run(() => StartCore(cancellationToken), CancellationToken.None);
        }
        try { await start.ConfigureAwait(false); }
        catch (Exception original)
        {
            try { await CloseAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
            ExceptionDispatchInfo.Capture(original).Throw();
        }
    }
    private void StartCore(CancellationToken cancellationToken)
    {
        Checkpoint("starting");
        _inputPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        _outputPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        Checkpoint("pipes");
        Marshal.ThrowExceptionForHR(ConptyNative.ConptyCreatePseudoConsole(new ConptyNative.Coord(80, 25), _inputPipe.ClientSafePipeHandle, _outputPipe.ClientSafePipeHandle, 0, out _console));
        _inputPipe.DisposeLocalCopyOfClientHandle();
        _outputPipe.DisposeLocalCopyOfClientHandle();
        OutputCompletion = Task.Run(() => _output.PumpAsync(_outputPipe), CancellationToken.None);
        Checkpoint("pseudoconsole");
        var process = ConptyNative.Start(_executablePath, WindowsArguments.Build(_executablePath, _arguments), _workingDirectory, _console);
        _process = process.Handle;
        ProcessId = process.Id;
        ProcessCompletion = Task.Run(() => { ExitCode = ConptyNative.WaitForExit(_process); }, CancellationToken.None);
        Checkpoint("process");
        Marshal.ThrowExceptionForHR(ConptyNative.ConptyReleasePseudoConsole(_console));
        Checkpoint("pump");
        lock (_gate)
        {
            _stop.Token.ThrowIfCancellationRequested();
            _running = true;
            _resize = ResizeLoopAsync();
        }
        return;

        void Checkpoint(string phase)
        {
            AcquisitionObserved?.Invoke(phase);
            cancellationToken.ThrowIfCancellationRequested();
            _stop.Token.ThrowIfCancellationRequested();
        }
    }
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRunning();
            if (input.Length > BoundedOutputStream.BlockSize) throw new ArgumentOutOfRangeException(nameof(input), "Input block exceeds 64KiB.");
            _inputs.RemoveWhere(task => task.IsCompleted);
            if (_inputs.Count >= 4) throw new InvalidOperationException("Input admission is full (four blocks); retry after consumption.");
            // Standard asynchronous Stream contract: caller retains an unchanged memory block until completion.
            // The bridge encodes once; this connection retains at most four admitted blocks, without a second copy.
            var owned = MemoryMarshal.TryGetArray(input, out var segment) && segment.Array is { Length: <= BoundedOutputStream.BlockSize }
                ? input : input.ToArray();
            var task = WriteAsync();
            _inputs.Add(task);
            return new ValueTask(task);

            async Task WriteAsync()
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
                var entered = false;
                try
                {
                    await _inputSerial.WaitAsync(linked.Token).ConfigureAwait(false);
                    entered = true;
                    await _inputPipe!.WriteAsync(owned, linked.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lock (_gate) _inputFailure ??= exception;
                    throw;
                }
                finally { if (entered) _inputSerial.Release(); }
            }
        }
    }
    public ValueTask ResizeAsync(ushort columns, ushort rows, CancellationToken cancellationToken)
    {
        if (columns is 0 or > 32767 || rows is 0 or > 32767) throw new ArgumentOutOfRangeException(nameof(columns), "COORD dimensions must be 1..32767.");
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRunning();
            _pendingSize = (columns, rows);
            _resizeAcknowledged ??= NewSignal();
            _resizeRequested.TrySetResult();
            return new ValueTask(_resizeAcknowledged.Task);
        }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private async Task ResizeLoopAsync()
    {
        try
        {
            while (true)
            {
                Task requested;
                lock (_gate) requested = _resizeRequested.Task;
                await requested.WaitAsync(_stop.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    var size = _pendingSize!.Value;
                    Marshal.ThrowExceptionForHR(ConptyNative.ConptyResizePseudoConsole(_console!, new ConptyNative.Coord(checked((short)size.Columns), checked((short)size.Rows))));
                    _pendingSize = null;
                    var acknowledged = _resizeAcknowledged!;
                    _resizeAcknowledged = null;
                    _resizeRequested = NewSignal();
                    acknowledged.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            lock (_gate) _resizeAcknowledged?.TrySetCanceled();
        }
        catch (Exception exception)
        {
            lock (_gate) _resizeAcknowledged?.TrySetException(exception);
            throw;
        }
    }
    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        if (!_running) throw new InvalidOperationException("Connection has not completed startup.");
    }
    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_close is not null) return new ValueTask(_close);
            cancellationToken.ThrowIfCancellationRequested();
            _closing = true;
            _output.DiscardRemaining();
            _stop.Cancel();
            _close = Task.Run(CloseCoreAsync, CancellationToken.None);
            return new ValueTask(_close);
        }
    }
    private async Task CloseCoreAsync()
    {
        var failures = new List<Exception>();
        if (_start is not null) await Join(_start, startup: true).ConfigureAwait(false);
        Task[] inputs;
        lock (_gate) inputs = [.. _inputs];
        foreach (var input in inputs) await Join(input).ConfigureAwait(false);
        if (_resize is not null) await Join(_resize).ConfigureAwait(false);
        if (_inputFailure is not null && !failures.Contains(_inputFailure)) failures.Add(_inputFailure);
        Release(() => { _inputPipe?.Dispose(); _inputPipe = null; });
        Release(() => _outputPipe?.DisposeLocalCopyOfClientHandle());
        if (_console is { IsClosed: false }) Release(() => { _console.Dispose(); NativeCloseCount++; });
        var terminationError = 0;
        if (_running && !ProcessCompletion.IsCompleted)
        {
            // Closing HPCON signals attached clients; it does not acknowledge their exit.
            // Keep the sole output drainer alive while allowing normal CTRL_CLOSE cleanup.
            try { await ProcessCompletion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (_process is { IsClosed: false } && !ProcessCompletion.IsCompleted)
            Release(() =>
            {
                ProcessTerminationRequested = true;
                terminationError = ConptyNative.RequestTermination(_process, 1);
            });
        await Join(OutputCompletion).ConfigureAwait(false);
        Release(_output.ReleaseBuffersAfterProducer);
        await Join(ProcessCompletion).ConfigureAwait(false);
        if (terminationError != 0) Release(() => ConptyNative.VerifyTermination(_process!, terminationError));
        Release(() => { _outputPipe?.Dispose(); _outputPipe = null; });
        Release(() => _process?.Dispose());
        _stop.Dispose();
        _inputSerial.Dispose();
        switch (failures.Count)
        {
            case 0: break;
            case 1: ExceptionDispatchInfo.Capture(failures[0]).Throw(); break;
            default: throw new AggregateException(failures);
        }
        return;

        async Task Join(Task task, bool startup = false)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception exception) { if (!startup && exception is not OperationCanceledException) failures.Add(exception); }
        }
        void Release(Action action) { try { action(); } catch (Exception exception) { failures.Add(exception); } }
    }
    public ValueTask DisposeAsync() => CloseAsync(CancellationToken.None);
}
