using System.Security.Cryptography.X509Certificates;
using WinShell.Core.Connections;
using WinShell.Core.Credentials;
using WinShell.Core.Files;
using WinShell.Core.Profiles;
namespace WinShell.Infrastructure.Ftp;

public sealed class FtpSessionFactory(ICredentialStore credentials, ConnectionAdmissionBudget budget, IConnectionProfileRepository profiles) : IRemoteFileSessionFactory
{
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    internal X509ChainPolicy? CertificateChainPolicy
    {
        get; set;
    }
    public async Task<RemoteFileConnectResult> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken, bool allowPlaintext = false)
    {
        if (ConnectionProfileValidator.Validate(profile).Count != 0 || profile.Protocol is not (ConnectionProtocol.Ftp or ConnectionProtocol.Ftps) || !Enum.IsDefined(profile.FtpsMode))
            throw new ArgumentException("FTP 需要有效的 FTP / FTPS 配置。");
        if (profile.Protocol == ConnectionProtocol.Ftp && !allowPlaintext)
            throw new InvalidOperationException("必须先确认 FTP 明文传输风险。");
        var session = new FtpFileSession(profile, credentials, budget, profiles, Timeout, CertificateChainPolicy);
        try
        {
            await session.ValidateAsync(cancellationToken).ConfigureAwait(false);
            return new RemoteFileConnectResult.Connected(session);
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
