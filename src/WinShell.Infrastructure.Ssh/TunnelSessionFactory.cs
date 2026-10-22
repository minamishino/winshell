using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using WinShell.Core.Tunnels;
namespace WinShell.Infrastructure.Ssh;
public sealed class TunnelSessionFactory(ISshSessionFactory sshFactory, TunnelAdmissionBudget budget) : ITunnelFactory
{
    public async Task<TunnelStartResult> StartAsync(ConnectionProfile profile, TunnelDefinition definition, CancellationToken cancellationToken)
    {
        TunnelDefinitionValidator.Validate(definition);
        if (profile.Id != definition.ProfileId || profile.Protocol != ConnectionProtocol.Ssh) throw new ArgumentException("隧道必须引用所选 SSH 配置。");
        cancellationToken.ThrowIfCancellationRequested(); var admission = budget.Admit(); ISshSession? session = null; var transferred = false;
        try {
            var result = await sshFactory.ConnectAsync(profile, cancellationToken).ConfigureAwait(false);
            if (result is SshConnectResult.UnknownHostKey challenge) return new TunnelStartResult.UnknownHostKey(challenge.Endpoint, challenge.Key);
            session = ((SshConnectResult.Connected)result).Session;
            if (session is not ISshForwardingSession forwarding) throw new InvalidOperationException("SSH 适配器不支持隧道。");
            var tunnel = await forwarding.StartTunnelAsync(definition, cancellationToken).ConfigureAwait(false);
            var owned = new AdmittedTunnel(tunnel, admission); transferred = true; return new TunnelStartResult.Started(owned);
        }
        finally { if (!transferred) { try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); } finally { admission.Dispose(); } } }
    }
    private sealed class AdmittedTunnel(ITunnelSession session, IDisposable admission) : ITunnelSession
    {
        private readonly Lock _gate = new(); private Task? _close;
        public TunnelDefinition Definition => session.Definition;
        public ushort BoundPort => session.BoundPort;
        public TunnelSnapshot Snapshot => session.Snapshot;
        public Task CloseAsync(CancellationToken cancellationToken) { lock (_gate) return _close ??= CloseCoreAsync(); }
        private async Task CloseCoreAsync() { try { await session.DisposeAsync().ConfigureAwait(false); } finally { admission.Dispose(); } }
        public ValueTask DisposeAsync() => new(CloseAsync(CancellationToken.None));
    }
}
