namespace WinShell.Core.Credentials;

public interface ICredentialStore
{
    Task<CredentialSecret?> ReadAsync(Guid profileId, CancellationToken cancellationToken);
    Task WriteAsync(Guid profileId, ReadOnlyMemory<byte> secretUtf8, CancellationToken cancellationToken);
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken);
}
