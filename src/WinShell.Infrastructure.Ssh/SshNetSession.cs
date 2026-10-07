using Renci.SshNet;
using Renci.SshNet.Common;
using WinShell.Core.Ssh;
using SshConnectionException = WinShell.Core.Ssh.SshConnectionException;

namespace WinShell.Infrastructure.Ssh;

internal sealed class SshNetSession : ISshScriptSession, ISshTerminalSession
{
    private SshTerminalSession? _terminal;
    private readonly CancellationTokenSource _scriptStop = new();
    private Task<SshScriptResult>? _scriptCore;
    private Exception? _scriptSessionFailure;
    internal TimeSpan ScriptTimeout { get; set; } = TimeSpan.FromSeconds(30);
    private readonly SshClient _client;
    private readonly IDisposable? _admission;
    private readonly SshHostKeyPolicy _hostKey;
    private readonly object _closeLock = new();
    private readonly TaskCompletionSource _connectionSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _closeTask;
    private readonly CancellationTokenSource _watchCancellation = new();
    private Task _watchTask = Task.CompletedTask;

    internal SshNetSession(SshClient client, SshHostKey? trustedKey, IDisposable? admission = null)
    {
        _client = client;
        _admission = admission;
        _hostKey = new SshHostKeyPolicy(trustedKey);
        _client.HostKeyReceived += _hostKey.OnReceived;
        _client.ErrorOccurred += OnErrorOccurred;
    }

    public bool IsConnected { get { lock (_closeLock) return _closeTask is null && _client.IsConnected; } }
    public event EventHandler<SshSessionClosedEventArgs>? Closed;
    internal SshHostKey? ObservedKey => _hostKey.ObservedKey;
    internal SshConnectionException? HostKeyFailure => _hostKey.Failure;

    internal async Task ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            lock (_closeLock)
                if (_closeTask is null) _watchTask = WatchConnectionAsync();
        }
        finally { _connectionSettled.TrySetResult(); }
    }

    private async Task WatchConnectionAsync()
    {
        // BaseClient has no public Disconnected event, and graceful peer shutdown need not raise ErrorOccurred.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(_watchCancellation.Token).ConfigureAwait(false))
            {
                lock (_closeLock)
                {
                    if (_closeTask is not null || _scriptCore is not null || _terminal?.HasStarted == true) return;
                    if (_client.IsConnected) continue;
                }
                _ = BeginClose(new SshConnectionException(SshFailureKind.Network, "SSH 远端已关闭连接。"));
                return;
            }
        }
        catch (OperationCanceledException) when (_watchCancellation.IsCancellationRequested) { }
    }

    private void OnErrorOccurred(object? sender, ExceptionEventArgs args)
    {
        // Never disconnect/dispose on a protocol worker: library disposal may join that very worker.
        lock (_closeLock)
        {
            if (_terminal is not null)
            {
                _terminal.ObserveFailure(HostKeyFailure ?? SshSessionFactory.Classify(args.Exception));
                // Before deferred Start there is no Shell monitor to own close.
                if (_terminal.HasStarted) return;
            }
            if (_scriptCore is not null)
            {
                // Exclusive close joins the listener but reports an aggregate. Retain its original cause for classification.
                _scriptSessionFailure ??= args.Exception;
                return;
            }
        }
        _ = BeginClose(HostKeyFailure ?? SshSessionFactory.Classify(args.Exception));
    }

    public Task<SshScriptResult> RunScriptAsync(string script, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.Contains('\0')) throw new ArgumentException("SSH 脚本不能包含 NUL。", nameof(script));
        var encoding = new System.Text.UTF8Encoding(false, true);
        int byteCount;
        try { byteCount = encoding.GetByteCount(script); }
        catch (System.Text.EncoderFallbackException error) { throw new ArgumentException("SSH 脚本包含非法 Unicode。", nameof(script), error); }
        if (byteCount > 8192) throw new ArgumentException("SSH 脚本不能超过 8192 UTF-8 字节。", nameof(script));
        lock (_closeLock)
        {
            if (_scriptCore is not null || _terminal is not null || _closeTask is not null) throw new InvalidOperationException("SSH 连接只能用于一次脚本或终端。");
            cancellationToken.ThrowIfCancellationRequested();
            var scriptBytes = encoding.GetBytes(script);
            // Publish the owned core before any worker can close the session. The outer wrapper is never joined by close.
            _scriptCore = Task.Run(() => RunScriptCoreAsync(scriptBytes, cancellationToken));
            return CompleteScriptAsync(_scriptCore);
        }
    }

    private async Task<SshScriptResult> CompleteScriptAsync(Task<SshScriptResult> core)
    {
        Exception? failure = null;
        try { return await core.ConfigureAwait(false); }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await BeginClose(failure as SshConnectionException).ConfigureAwait(false); }
            catch (Exception cleanupError) when (failure is not null) { failure.Data["SshSessionCleanupFailure"] = cleanupError; }
        }
    }

    private async Task<SshScriptResult> RunScriptCoreAsync(byte[] scriptBytes, CancellationToken callerToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _scriptStop.Token);
        deadline.CancelAfter(ScriptTimeout);
        using var input = new MemoryStream(scriptBytes, writable: false);
        using var output = new SshScriptCapture(32768);
        using var errorOutput = new SshScriptCapture(16384);
        Shell? shell = null;
        Task? starting = null;
        Exception? failure = null;
        try
        {
            shell = _client.CreateShellNoTerminal(input, output, errorOutput, 1024);
            var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            starting = shell.StartExclusiveSessionAsync(() => closing.TrySetResult(), deadline.Token);
            await starting.ConfigureAwait(false);
            await closing.Task.ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (shell is not null && starting is not null)
            {
                try { await shell.CloseExclusiveSessionAsync().ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
                try { await starting.ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
                try { await shell.InputCompletion.ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
            }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(scriptBytes);
        }
        try
        {
            // A sink exception may reach the session callback after startup failed. Preserve the original instance.
            if (output.Failure is not null) throw output.Failure;
            if (errorOutput.Failure is not null) throw errorOutput.Failure;
            if (HostKeyFailure is not null) throw HostKeyFailure;
            if (callerToken.IsCancellationRequested || _scriptStop.IsCancellationRequested)
                throw new OperationCanceledException("SSH 脚本已取消。", failure, callerToken.IsCancellationRequested ? callerToken : _scriptStop.Token);
            if (deadline.IsCancellationRequested)
                throw new SshConnectionException(SshFailureKind.Timeout, "SSH 脚本执行超时。", failure);
            var sessionFailure = Volatile.Read(ref _scriptSessionFailure);
            if (sessionFailure is not null)
            {
                var classifiedFailure = SshSessionFactory.Classify(sessionFailure);
                if (failure is not null && !ReferenceEquals(failure, sessionFailure))
                    classifiedFailure.Data["SshExclusiveCleanupFailure"] = failure;
                throw classifiedFailure;
            }
            if (failure is not null) throw SshSessionFactory.Classify(failure);
            if (shell is null || !shell.RemoteClosed)
                throw new SshConnectionException(SshFailureKind.Network, "SSH 脚本连接在远端通道结束前中断。");
            return new SshScriptResult(output.Decode(), errorOutput.Decode(), shell.ExitStatus, shell.ExitSignal, shell.RemoteEofReceived);
        }
        finally { shell?.Dispose(); }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        // Once close is requested, cleanup must finish even when its caller cancels.
        return BeginClose(null);
    }

    public ValueTask DisposeAsync() => new(BeginClose(null));

    public WinShell.Core.Terminal.IShellSession CreateTerminalSession(ushort columns, ushort rows)
    {
        SshTerminalSession.ValidateSize(columns, rows);
        lock (_closeLock)
        {
            if (_terminal is not null || _scriptCore is not null || _closeTask is not null || !_client.IsConnected)
                throw new InvalidOperationException("SSH 连接只能用于一次脚本或终端。");
            return _terminal = new(_client, columns, rows, BeginClose);
        }
    }

    private Task BeginClose(SshConnectionException? failure)
    {
        lock (_closeLock)
            return _closeTask ??= Task.Run(async () =>
            {
                await _connectionSettled.Task.ConfigureAwait(false);
                _terminal?.RequestStop();
                await _watchCancellation.CancelAsync().ConfigureAwait(false);
                await _watchTask.ConfigureAwait(false);
                _watchCancellation.Dispose();
                await _scriptStop.CancelAsync().ConfigureAwait(false);
                if (_scriptCore is not null)
                {
                    try { await _scriptCore.ConfigureAwait(false); }
                    catch (Exception) { /* The RunScriptAsync caller observes the execution failure. */ }
                }
                _scriptStop.Dispose();
                Exception? terminalFailure = null;
                if (_terminal is not null)
                {
                    try { await _terminal.JoinOwnedAsync().ConfigureAwait(false); }
                    catch (Exception error) { terminalFailure = error; }
                }
                _client.ErrorOccurred -= OnErrorOccurred;
                try { _client.Dispose(); }
                finally
                {
                    _client.HostKeyReceived -= _hostKey.OnReceived;
                    _admission?.Dispose();
                    Closed?.Invoke(this, new SshSessionClosedEventArgs(failure));
                }
                if (terminalFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminalFailure).Throw();
            });
    }
}
