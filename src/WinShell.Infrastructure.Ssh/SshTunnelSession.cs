using System.Net;
using Renci.SshNet;
using Renci.SshNet.Common;
using WinShell.Core.Ssh;
using WinShell.Core.Tunnels;
using SshConnectionException = WinShell.Core.Ssh.SshConnectionException;
namespace WinShell.Infrastructure.Ssh;

internal sealed class SshTunnelSession : ITunnelSession
{
    private readonly ForwardedPort _port;
    private readonly Func<SshConnectionException?, Task> _close;
    private int _state = (int)TunnelState.Starting;
    private int _failure = -1;
    public TunnelDefinition Definition { get; }
    public ushort BoundPort { get; private set; }
    public TunnelSnapshot Snapshot => new((TunnelState)Volatile.Read(ref _state), _port.ActiveForwardingConnections, Volatile.Read(ref _failure) is var failure && failure >= 0 ? (SshFailureKind)failure : null);
    internal Task Startup { get; private set; } = Task.CompletedTask;
    internal Task Completion => _port.ForwardingCompletion;
    internal SshTunnelSession(SshClient client, TunnelDefinition definition, Func<SshConnectionException?, Task> close)
    {
        Definition = definition; _close = close;
        _port = definition.Kind switch {
            TunnelKind.Local => new ForwardedPortLocal("127.0.0.1", definition.BoundPort, definition.TargetHost, definition.TargetPort),
            TunnelKind.Remote => new ForwardedPortRemote(IPAddress.Loopback, definition.BoundPort, IPAddress.Parse(definition.TargetHost), definition.TargetPort),
            TunnelKind.Dynamic => new ForwardedPortDynamic("127.0.0.1", definition.BoundPort),
            _ => throw new ArgumentOutOfRangeException(nameof(definition))
        };
        _port.ConfigureBoundedLifetime(4); _port.Exception += OnException; client.AddForwardedPort(_port);
    }
    internal Task<ITunnelSession> StartAsync(CancellationToken cancellationToken)
    {
        Startup = Task.Run(() => {
            cancellationToken.ThrowIfCancellationRequested(); _port.Start();
            var actual = _port switch { ForwardedPortLocal local => local.BoundPort, ForwardedPortRemote remote => remote.BoundPort, ForwardedPortDynamic dynamicPort => dynamicPort.BoundPort, _ => 0u };
            if (actual is 0 or > 65535) throw new SshConnectionException(SshFailureKind.Protocol, "服务器未返回有效的实际绑定端口。");
            BoundPort = (ushort)actual;
            Interlocked.CompareExchange(ref _state, (int)TunnelState.Running, (int)TunnelState.Starting);
        }, CancellationToken.None);
        return FinishStartAsync(cancellationToken);
    }
    private async Task<ITunnelSession> FinishStartAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => _ = _close(null));
        try { await Startup.ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); return this; }
        catch (Exception error) {
            try { await _close(error as SshConnectionException).ConfigureAwait(false); }
            catch (Exception cleanup) { error.Data["TunnelCleanupFailure"] = cleanup; }
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("隧道启动已取消。", error, cancellationToken);
            throw SshSessionFactory.Classify(error);
        }
    }
    // A failed individual target/SOCKS handshake does not tear down unrelated forwarding connections.
    private void OnException(object? sender, ExceptionEventArgs args) => Volatile.Write(ref _failure, (int)SshSessionFactory.Classify(args.Exception).Kind);
    internal void StopAdmission() { Interlocked.Exchange(ref _state, (int)TunnelState.Closing); _port.StopBoundedForwarding(); }
    internal void DisposePort() { _port.Exception -= OnException; _port.Dispose(); Volatile.Write(ref _state, (int)TunnelState.Closed); }
    public Task CloseAsync(CancellationToken cancellationToken) => _close(null);
    public ValueTask DisposeAsync() => new(_close(null));
}
