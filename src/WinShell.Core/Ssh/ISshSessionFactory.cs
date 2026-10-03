using WinShell.Core.Profiles;

namespace WinShell.Core.Ssh;

public interface ISshSessionFactory
{
    /// <summary>Returns an authenticated owned session or a key challenge with no retained connection.</summary>
    Task<SshConnectResult> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken);
}
