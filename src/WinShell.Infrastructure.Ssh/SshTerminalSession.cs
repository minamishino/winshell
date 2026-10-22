using Renci.SshNet;
using WinShell.Core.Ssh;
using WinShell.Core.Terminal;
using SshConnectionException = WinShell.Core.Ssh.SshConnectionException;

namespace WinShell.Infrastructure.Ssh;

internal sealed class SshTerminalSession(SshClient client, ushort initialColumns, ushort initialRows,
    Func<SshConnectionException?, Task> closeOwner, string? startupCommand = null) : IShellSession, IShellSessionExit
{
    private readonly Lock _gate = new();
    private readonly BorrowedInputStream _input = new();
    private readonly SshOutputStream _output = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Shell? _shell;
    private Task? _start, _join;
    private bool _closing, _disposed;
    private SshConnectionException? _failure;
    public Stream Output => _output;
    public Task OutputCompletion { get; private set; } = Task.CompletedTask;
    public Task ProcessCompletion => OutputCompletion;
    public int? ExitCode => ExitStatus;
    internal bool HasStarted { get { lock (_gate) return _start is not null; } }
    internal int? ExitStatus => _shell?.ExitStatus;
    internal bool RemoteClosed => _shell?.RemoteClosed ?? false;
    internal int MaximumOutputBlocks => _output.MaximumOwnedBlocks;
    internal Task Backpressured => _output.Backpressured.Task;

    public Task StartAsync(CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_start is not null) throw new InvalidOperationException("终端只能启动一次。");
            return _start = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
                try
                {
                    Task starting;
                    lock (_gate)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        _shell = client.CreateShell(_input, _output, _output, "xterm-256color", initialColumns, initialRows, 0, 0,
                            new Dictionary<Renci.SshNet.Common.TerminalModes, uint>(), 16384);
                        starting = _shell.StartExclusiveSessionAsync(WakeForShutdown, startupCommand, _stop.Token);
                        OutputCompletion = MonitorAsync(_shell);
                    }
                    // Caller cancellation is a local close, not a remote transport loss.
                    await using var registration = token.Register(RequestStop);
                    await starting.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    var failure = token.IsCancellationRequested || _stop.IsCancellationRequested ? error : SshSessionFactory.Classify(error);
                    if (failure is SshConnectionException classified) ObserveFailure(classified);
                    _output.Complete(failure);
                    throw failure;
                }
            }, CancellationToken.None);
        }
    }

    internal void ObserveFailure(SshConnectionException failure)
    {
        lock (_gate) _failure ??= failure;
        // Never wait or dispose a client from its protocol callback.
    }
    private void WakeForShutdown()
    {
        lock (_gate) _output.StopWriting(_closing);
        _input.Dispose();
        _shutdown.TrySetResult();
    }
    private async Task MonitorAsync(Shell shell)
    {
        Exception? cleanup = null;
        try
        {
            while (!_shutdown.Task.IsCompleted)
            {
                if (shell.RemoteEofReceived) _output.Complete();
                await Task.WhenAny(_shutdown.Task, Task.Delay(20)).ConfigureAwait(false);
            }
            await shell.CloseExclusiveSessionAsync().ConfigureAwait(false);
        }
        catch (Exception error) { cleanup = error; }
        SshConnectionException? failure;
        lock (_gate)
        {
            failure = _failure;
            if (failure is null && cleanup is not null && !_closing) failure = SshSessionFactory.Classify(cleanup);
            if (failure is null && !_closing && !shell.RemoteClosed)
                failure = new SshConnectionException(SshFailureKind.Network, "SSH 终端连接在通道结束前中断。");
            _failure ??= failure;
        }
        if (failure is not null && cleanup is not null) failure.Data["SshTerminalCleanupFailure"] = cleanup;
        _output.Complete(failure);
        if (failure is not null) throw failure;
        if (cleanup is not null) throw cleanup;
    }
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        lock (_gate)
        {
            if (_closing || _shell?.IsStarted != true) throw new InvalidOperationException("SSH 终端尚未运行。");
            return _input.EnqueueAsync(bytes, token);
        }
    }
    public async ValueTask ResizeAsync(ushort columns, ushort rows, CancellationToken token)
    {
        ValidateSize(columns, rows);
        Task resize;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_closing || _shell?.IsStarted != true) throw new InvalidOperationException("SSH 终端尚未运行。");
            resize = _shell.ChangeWindowSizeAsync(columns, rows, 0, 0);
        }
        await resize.ConfigureAwait(false);
    }
    internal static void ValidateSize(ushort columns, ushort rows)
    {
        if (columns is 0 or > 400 || rows is 0 or > 160) throw new ArgumentOutOfRangeException(nameof(columns), "终端尺寸范围为1..400列、1..160行。");
    }
    internal static void ValidateStartupCommand(string? command)
    {
        if (command is null) return;
        if (command.Length == 0 || command.Length > 8192 || command.Contains('\0') || new System.Text.UTF8Encoding(false, true).GetByteCount(command) > 8192)
            throw new ArgumentException("终端启动命令必须为1..8192个有效 UTF-8 字节，且不含 NUL。", nameof(command));
    }
    public ValueTask CloseAsync(CancellationToken token)
    {
        RequestStop();
        return new ValueTask(closeOwner(_failure));
    }
    internal void RequestStop()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            _output.StopWriting(discard: true);
            // Mark the mature exclusive owner closing before waking its borrowed input reader.
            // Otherwise the pump can observe disposal as a new failure before its cancel callback runs.
            _stop.Cancel();
            _input.Dispose();
            _shutdown.TrySetResult();
        }
    }
    internal Task JoinOwnedAsync()
    {
        RequestStop();
        lock (_gate) return _join ??= Task.Run(async () =>
        {
            if (_start is not null) try { await _start.ConfigureAwait(false); } catch (Exception) { /* Start caller observes startup failure. */ }
            Exception? failure = null;
            try { if (_shell is not null) await _shell.CloseExclusiveSessionAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            try { await OutputCompletion.ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            _output.Complete(_failure ?? failure);
            try { _shell?.Dispose(); }
            finally { ReleaseStreams(); }
            if (failure is not null) throw failure;
        });
    }
    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            ReleaseStreams();
        }
    }
    private void ReleaseStreams()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _output.Dispose(); _input.Dispose(); _stop.Dispose();
        }
    }
}
