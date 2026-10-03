namespace WinShell.Core.Profiles;

public interface IConnectionProfileRepository
{
    Task<ProfileReadResult> ReadAsync(CancellationToken cancellationToken);
    Task UpsertAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid profileId, CancellationToken cancellationToken);
}
