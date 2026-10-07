using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.Core.Mcp;

/// <summary>Every call reads current grants, saved configuration and trusted key; never reads secrets.</summary>
public sealed class McpPermissionReader(IMcpGrantRepository grants, IConnectionProfileRepository profiles, IKnownHostRepository hosts)
{
    public async Task<IReadOnlyList<McpAuthorizedProfile>> ReadAsync(string scope, CancellationToken cancellationToken)
    {
        if (scope is not (McpGrant.ProfileListScope or McpGrant.ScriptScope)) return [];
        var permissions = await grants.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (permissions.Count == 0) return [];
        if (permissions.Count > 64 || permissions.Select(grant => grant.ProfileId).Distinct().Count() != permissions.Count)
            throw new InvalidDataException("授权数量或标识无效。");
        var saved = await profiles.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (saved.RecoveredFromBackup || saved.Profiles.Select(profile => profile.Id).Distinct().Count() != saved.Profiles.Count)
            throw new InvalidDataException("配置无法安全用于授权。");
        var authorized = new List<McpAuthorizedProfile>();
        foreach (var permission in permissions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scope == McpGrant.ProfileListScope ? !permission.ListProfiles : !permission.RunScript) continue;
            var profile = saved.Profiles.SingleOrDefault(profile => profile.Id == permission.ProfileId);
            if (profile is null || profile.Protocol != ConnectionProtocol.Ssh || ConnectionProfileValidator.Validate(profile).Count != 0) continue;
            var key = await hosts.ReadAsync(new(profile.Host, profile.Port), cancellationToken).ConfigureAwait(false);
            if (key is not null && permission.Allows(profile, key, scope)) authorized.Add(new(profile, key));
        }
        return authorized;
    }
}
public sealed record McpAuthorizedProfile(ConnectionProfile Profile, SshHostKey HostKey);
