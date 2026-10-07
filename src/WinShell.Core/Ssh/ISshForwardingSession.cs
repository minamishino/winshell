using WinShell.Core.Tunnels;
namespace WinShell.Core.Ssh;
public interface ISshForwardingSession : ISshSession
{
    Task<ITunnelSession> StartTunnelAsync(TunnelDefinition definition, CancellationToken cancellationToken);
}
