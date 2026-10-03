namespace WinShell.Core.Ssh;

/// <summary>Owns one authenticated connection and all its resources. Disconnect and disposal must be idempotent.</summary>
public interface ISshSession : IAsyncDisposable
{
    bool IsConnected { get; }
    event EventHandler<SshSessionClosedEventArgs>? Closed;
    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <param name="failure">Null for a normal close; otherwise the classified disconnect cause.</param>
public sealed class SshSessionClosedEventArgs(SshConnectionException? failure = null) : EventArgs
{
    public SshConnectionException? Failure { get; } = failure;
}
