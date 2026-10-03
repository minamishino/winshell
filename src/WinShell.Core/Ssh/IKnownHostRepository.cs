namespace WinShell.Core.Ssh;

public interface IKnownHostRepository
{
    Task<SshHostKey?> ReadAsync(SshEndpointIdentity endpoint, CancellationToken cancellationToken);

    /// <summary>Atomically trusts an absent key or confirms the identical key; never replaces a different key.</summary>
    Task<bool> TrustIfAbsentOrSameAsync(SshEndpointIdentity endpoint, SshHostKey key, CancellationToken cancellationToken);
}
