using WinShell.Core.Files;
using WinShell.Core.Profiles;
using WinShell.Infrastructure.Ftp;
using WinShell.Infrastructure.Ssh;
namespace WinShell.App.Features.Files;
// Two real protocol consumers share one native Files page and DTO contract.
public sealed class RemoteFileSessionFactory(SftpSessionFactory sftp, FtpSessionFactory ftp) : IRemoteFileSessionFactory
{
    public Task<RemoteFileConnectResult> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken, bool allowPlaintext = false) => profile.Protocol switch
    {
        ConnectionProtocol.Ssh => sftp.ConnectAsync(profile, cancellationToken),
        ConnectionProtocol.Ftp or ConnectionProtocol.Ftps => ftp.ConnectAsync(profile, cancellationToken, allowPlaintext),
        _ => throw new ArgumentException("此配置不支持远端文件操作。")
    };
}
