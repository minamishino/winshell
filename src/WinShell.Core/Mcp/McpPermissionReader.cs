using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.Core.Mcp;

/// <summary>Every request validates current grants, saved profiles and full keys without reading secrets.</summary>
public sealed class McpPermissionReader(IMcpGrantRepository grants, IConnectionProfileRepository profiles, IKnownHostRepository hosts)
{
    private static readonly string[] Scopes = [McpGrant.ProfileListScope, McpGrant.ScriptScope,
        McpGrant.DockerReadScope, McpGrant.DockerControlScope, McpGrant.DockerExecScope];

    public async Task<IReadOnlyList<McpAuthorizedProfile>> ReadAsync(string scope, CancellationToken cancellationToken)
    {
        if (!Scopes.Contains(scope, StringComparer.Ordinal)) return [];
        var validated = await ReadBoundAsync(scope, cancellationToken).ConfigureAwait(false);
        return [.. validated.Select(item => item.Authorized)];
    }

    public async Task<IReadOnlySet<string>> ReadAvailableScopesAsync(CancellationToken cancellationToken)
    {
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (permission, _) in await ReadBoundAsync(null, cancellationToken).ConfigureAwait(false))
            foreach (var scope in Scopes)
                if (Enabled(permission, scope)) available.Add(scope);
        return available;
    }

    private async Task<List<(McpGrant Grant, McpAuthorizedProfile Authorized)>> ReadBoundAsync(string? scope, CancellationToken cancellationToken)
    {
        var permissions = await grants.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (permissions.Count == 0) return [];
        if (permissions.Count > 64 || permissions.Select(grant => grant.ProfileId).Distinct().Count() != permissions.Count)
            throw new InvalidDataException("授权数量或标识无效。");
        var saved = await profiles.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (saved.RecoveredFromBackup || saved.Profiles.Select(profile => profile.Id).Distinct().Count() != saved.Profiles.Count)
            throw new InvalidDataException("配置无法安全用于授权。");
        var authorized = new List<(McpGrant, McpAuthorizedProfile)>();
        foreach (var permission in permissions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requiredScope = scope ?? Scopes.FirstOrDefault(candidate => Enabled(permission, candidate));
            if (requiredScope is null || !Enabled(permission, requiredScope)) continue;
            var profile = saved.Profiles.SingleOrDefault(profile => profile.Id == permission.ProfileId);
            if (profile is null || profile.Protocol != ConnectionProtocol.Ssh || ConnectionProfileValidator.Validate(profile).Count != 0) continue;
            var key = await hosts.ReadAsync(new SshEndpointIdentity(profile.Host, profile.Port), cancellationToken).ConfigureAwait(false);
            if (key is not null && permission.Allows(profile, key, requiredScope)) authorized.Add((permission, new McpAuthorizedProfile(profile, key)));
        }
        return authorized;
    }
    private static bool Enabled(McpGrant permission, string scope) => scope switch
    {
        McpGrant.ProfileListScope => permission.ListProfiles, McpGrant.ScriptScope => permission.RunScript,
        McpGrant.DockerReadScope => permission.DockerRead, McpGrant.DockerControlScope => permission.DockerControl,
        McpGrant.DockerExecScope => permission.DockerExec, _ => false
    };
}
public sealed record McpAuthorizedProfile(ConnectionProfile Profile, SshHostKey HostKey);
