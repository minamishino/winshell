using Renci.SshNet;
using Renci.SshNet.Common;
using WinShell.Core.Ssh;
using SshConnectionException = WinShell.Core.Ssh.SshConnectionException;

namespace WinShell.Infrastructure.Ssh;

internal sealed class SshNetSession : ISshSession
{
    private readonly SshClient _client;
    private readonly SshHostKey? _trustedKey;
    private readonly object _closeLock = new();
    private readonly TaskCompletionSource _connectionSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _closeTask;
    private readonly CancellationTokenSource _watchCancellation = new();
    private Task _watchTask = Task.CompletedTask;

    internal SshNetSession(SshClient client, SshHostKey? trustedKey)
    {
        _client = client;
        _trustedKey = trustedKey;
        _client.HostKeyReceived += OnHostKeyReceived;
        _client.ErrorOccurred += OnErrorOccurred;
    }

    public bool IsConnected { get { lock (_closeLock) return _closeTask is null && _client.IsConnected; } }
    public event EventHandler<SshSessionClosedEventArgs>? Closed;
    internal SshHostKey? ObservedKey { get; private set; }
    internal SshConnectionException? HostKeyFailure { get; private set; }

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
                    if (_closeTask is not null) return;
                    if (_client.IsConnected) continue;
                }
                _ = BeginClose(new SshConnectionException(SshFailureKind.Network, "SSH 远端已关闭连接。"));
                return;
            }
        }
        catch (OperationCanceledException) when (_watchCancellation.IsCancellationRequested) { }
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs args)
    {
        args.CanTrust = false;
        try
        {
            var receivedKey = new SshHostKey(args.HostKeyName, args.HostKey);
            ObservedKey = receivedKey;
            if (_trustedKey is null) return;
            if (!_trustedKey.Equals(receivedKey))
            {
                HostKeyFailure = new SshConnectionException(SshFailureKind.HostKeyChanged, "SSH 主机公钥已改变；连接已拒绝。");
                return;
            }
            args.CanTrust = true;
        }
        catch (ArgumentException error)
        {
            HostKeyFailure = new SshConnectionException(SshFailureKind.Protocol, "SSH 主机公钥无效或超过限制。", error);
        }
    }

    private void OnErrorOccurred(object? sender, ExceptionEventArgs args)
    {
        // Never disconnect/dispose on a protocol worker: library disposal may join that very worker.
        _ = BeginClose(HostKeyFailure ?? SshSessionFactory.Classify(args.Exception));
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        // Once close is requested, cleanup must finish even when its caller cancels.
        return BeginClose(null);
    }

    public ValueTask DisposeAsync() => new(BeginClose(null));

    private Task BeginClose(SshConnectionException? failure)
    {
        lock (_closeLock)
            return _closeTask ??= Task.Run(async () =>
            {
                await _connectionSettled.Task.ConfigureAwait(false);
                await _watchCancellation.CancelAsync().ConfigureAwait(false);
                await _watchTask.ConfigureAwait(false);
                _watchCancellation.Dispose();
                _client.ErrorOccurred -= OnErrorOccurred;
                try { _client.Dispose(); }
                finally
                {
                    _client.HostKeyReceived -= OnHostKeyReceived;
                    Closed?.Invoke(this, new SshSessionClosedEventArgs(failure));
                }
            });
    }
}
