using WinShell.Core.Connections;
using WinShell.Core.Credentials;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Net.Sockets;
using SshConnectionException = WinShell.Core.Ssh.SshConnectionException;

namespace WinShell.Infrastructure.Ssh;

public sealed class SshSessionFactory : ISshSessionFactory
{
    private readonly IKnownHostRepository _knownHosts;
    private readonly ICredentialStore _credentials;
    private readonly TimeSpan _timeout;
    private readonly ConnectionAdmissionBudget _budget;

    public SshSessionFactory(IKnownHostRepository knownHosts, ICredentialStore credentials)
        : this(knownHosts, credentials, new ConnectionAdmissionBudget(), TimeSpan.FromSeconds(30)) { }

    public SshSessionFactory(IKnownHostRepository knownHosts, ICredentialStore credentials, ConnectionAdmissionBudget budget)
        : this(knownHosts, credentials, budget, TimeSpan.FromSeconds(30)) { }

    internal SshSessionFactory(IKnownHostRepository knownHosts, ICredentialStore credentials, TimeSpan timeout)
        : this(knownHosts, credentials, new ConnectionAdmissionBudget(), timeout) { }

    internal SshSessionFactory(IKnownHostRepository knownHosts, ICredentialStore credentials, ConnectionAdmissionBudget budget, TimeSpan timeout)
    {
        _knownHosts = knownHosts ?? throw new ArgumentNullException(nameof(knownHosts));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    public async Task<SshConnectResult> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = _budget.AdmitConnection();
        var transferred = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        try
        {
            using var handshake = await _budget.EnterHandshakeAsync(deadline.Token).ConfigureAwait(false);

            // The library includes synchronous key parsing and disconnect paths; never enter them on the UI thread.
            var result = await Task.Run(() => ConnectCoreAsync(profile, lease, deadline.Token), CancellationToken.None).ConfigureAwait(false);
            transferred = result is SshConnectResult.Connected;
            return result;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SshConnectionException(SshFailureKind.Timeout, "SSH 连接超时。", error);
        }
        finally
        {
            if (!transferred) lease.Dispose();
        }
    }

    private async Task<SshConnectResult> ConnectCoreAsync(ConnectionProfile profile, IDisposable lease, CancellationToken cancellationToken)
    {
        if (profile is null || ConnectionProfileValidator.Validate(profile).Count != 0 || profile.Protocol != ConnectionProtocol.Ssh)
            throw new SshConnectionException(SshFailureKind.InvalidProfile, "SSH 连接配置无效。");
        SshEndpointIdentity endpoint;
        try { endpoint = new SshEndpointIdentity(profile.Host, profile.Port); }
        catch (ArgumentException error) { throw new SshConnectionException(SshFailureKind.InvalidProfile, "SSH 主机地址无效。", error); }
        SshHostKey? trustedKey;
        try { trustedKey = await _knownHosts.ReadAsync(endpoint, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new SshConnectionException(SshFailureKind.Storage, "无法读取可信主机记录；连接已拒绝。", error); }

        using var authentication = trustedKey is null
            ? SshAuthenticationScope.CreateProbe(profile.UserName)
            : await SshAuthenticationScope.CreateAsync(profile, _credentials, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellableAuthentication = new CancellableAuthenticationMethod(authentication.Method, cancellationToken);
        var connection = new ConnectionInfo(endpoint.Host, endpoint.Port, profile.UserName, cancellableAuthentication) { Timeout = _timeout };
        var session = new SshNetSession(new SshClient(connection) { KeepAliveInterval = TimeSpan.FromSeconds(30) }, trustedKey, lease);
        var transferred = false;
        Exception? connectionFailure = null;
        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (trustedKey is null || !session.IsConnected)
                throw new SshConnectionException(SshFailureKind.Network, "SSH 连接在认证期间关闭。");
            transferred = true;
            return new SshConnectResult.Connected(session, trustedKey);
        }
        catch (Exception error)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                connectionFailure = new OperationCanceledException("SSH 连接已取消。", error, cancellationToken);
                throw connectionFailure;
            }
            if (session.HostKeyFailure is not null)
            {
                connectionFailure = session.HostKeyFailure;
                throw connectionFailure;
            }
            if (trustedKey is null && session.ObservedKey is not null)
                return new SshConnectResult.UnknownHostKey(endpoint, session.ObservedKey);
            connectionFailure = Classify(error);
            throw connectionFailure;
        }
        finally
        {
            // Wait for the owned connection and background close before releasing secrets or the handshake slot.
            if (!transferred)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupError) when (connectionFailure is not null)
                {
                    connectionFailure.Data["SshSessionCleanupFailure"] = cleanupError;
                }
            }
        }
    }

    internal static SshConnectionException Classify(Exception error) => error as SshConnectionException ?? error switch
    {
        SshAuthenticationException => new(SshFailureKind.Authentication, "SSH 认证失败，请检查用户名和凭据。", error),
        SshOperationTimeoutException or TimeoutException => new(SshFailureKind.Timeout, "SSH 连接超时。", error),
        SocketException or IOException => new(SshFailureKind.Network, "SSH 网络连接已中断。", error),
        Renci.SshNet.Common.SshConnectionException => new(SshFailureKind.Network, "SSH 网络连接已中断。", error),
        _ => new(SshFailureKind.Protocol, "SSH 协议操作失败。", error)
    };
}
