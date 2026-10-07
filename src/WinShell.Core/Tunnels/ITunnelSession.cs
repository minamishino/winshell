using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.Core.Tunnels;
public enum TunnelState { Starting, Running, Closing, Closed, Failed }
public sealed record TunnelSnapshot(TunnelState State, int ActiveConnections, SshFailureKind? Failure);
public interface ITunnelSession : IAsyncDisposable
{
    TunnelDefinition Definition { get; }
    ushort BoundPort { get; }
    TunnelSnapshot Snapshot { get; }
    Task CloseAsync(CancellationToken cancellationToken);
}
public interface ITunnelFactory
{
    Task<TunnelStartResult> StartAsync(ConnectionProfile profile, TunnelDefinition definition, CancellationToken cancellationToken);
}
public abstract record TunnelStartResult
{
    private TunnelStartResult() { }
    public sealed record Started(ITunnelSession Session) : TunnelStartResult;
    public sealed record UnknownHostKey(SshEndpointIdentity Endpoint, SshHostKey Key) : TunnelStartResult;
}
