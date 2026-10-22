using Renci.SshNet;
using WinShell.Core.Connections;
using WinShell.Core.Credentials;
using WinShell.Core.Files;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.Infrastructure.Ssh;

public sealed class SftpSessionFactory(IKnownHostRepository knownHosts, ICredentialStore credentials, ConnectionAdmissionBudget budget) : IRemoteFileSessionFactory
{
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public async Task<RemoteFileConnectResult> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken, bool allowPlaintext = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = budget.AdmitConnection();
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        // The worker owns its deadline and admission until connection failure or transfer to the session.
        return await Task.Run(async () =>
        {
            using (deadline)
            {
                var transferred = false;
                try
                {
                    using var handshake = await budget.EnterHandshakeAsync(deadline.Token).ConfigureAwait(false);
                    var result = await ConnectCoreAsync(profile, lease, deadline.Token).ConfigureAwait(false);
                    transferred = result is RemoteFileConnectResult.Connected;
                    return result;
                }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
                { throw new SshConnectionException(SshFailureKind.Timeout, "SFTP 连接超时。", error); }
                finally { if (!transferred) lease.Dispose(); }
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }
    private async Task<RemoteFileConnectResult> ConnectCoreAsync(ConnectionProfile? profile, IDisposable admission, CancellationToken token)
    {
        if (profile is null || ConnectionProfileValidator.Validate(profile).Count != 0 || profile.Protocol != ConnectionProtocol.Ssh)
            throw new SshConnectionException(SshFailureKind.InvalidProfile, "SFTP 需要有效的 SSH 配置。");
        var endpoint = new SshEndpointIdentity(profile.Host, profile.Port);
        SshHostKey? trusted;
        try { trusted = await knownHosts.ReadAsync(endpoint, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new SshConnectionException(SshFailureKind.Storage, "无法读取可信主机记录；连接已拒绝。", error); }
        using Renci.SshNet.AuthenticationMethod authentication = trusted is null
            ? new NoneAuthenticationMethod(profile.UserName)
            : new DeferredAuthenticationMethod(profile, credentials, token);
        token.ThrowIfCancellationRequested();
        using var cancellableAuthentication = new CancellableAuthenticationMethod(authentication, token);
        var connection = new ConnectionInfo(endpoint.Host, endpoint.Port, profile.UserName, cancellableAuthentication) { Timeout = Timeout };
        // The verified receive budgets assume uncompressed SSH payloads.
        connection.CompressionAlgorithms.Remove("zlib@openssh.com");
        var client = new SftpClient(connection) { OperationTimeout = Timeout, BufferSize = 32768, KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var policy = new SshHostKeyPolicy(trusted);
        client.HostKeyReceived += policy.OnReceived;
        var transferred = false;
        Exception? failure = null;
        try
        {
            await client.ConnectAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (trusted is null || !client.IsConnected) throw new SshConnectionException(SshFailureKind.Network, "SFTP 连接在认证期间关闭。");
            var session = new SftpSession(client, policy, budget, admission);
            transferred = true;
            return new RemoteFileConnectResult.Connected(session, trusted);
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) throw failure = new OperationCanceledException("SFTP 连接已取消。", error, token);
            if (policy.Failure is not null) throw failure = policy.Failure;
            if (trusted is null && policy.ObservedKey is not null) return new RemoteFileConnectResult.UnknownHostKey(endpoint, policy.ObservedKey);
            throw failure = SshSessionFactory.Classify(error);
        }
        finally
        {
            if (!transferred)
            {
                try { client.Dispose(); }
                catch (Exception cleanup) when (failure is not null) { failure.Data["SftpCleanupFailure"] = cleanup; }
                finally { client.HostKeyReceived -= policy.OnReceived; }
            }
        }
    }
}
