using System.Collections.Frozen;

namespace WinShell.Mcp.Authorization;

/// <summary>Explicit, immutable grants. A stored profile is never an implicit grant.</summary>
public sealed class McpGrantSnapshot
{
    public const string ProfileListScope = "ssh.list_profiles";
    private readonly FrozenSet<Guid> _profileIds;
    public static McpGrantSnapshot Empty { get; } = new([]);
    public int Count => _profileIds.Count;

    public McpGrantSnapshot(IEnumerable<(Guid ProfileId, string Scope)> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var profileIds = new HashSet<Guid>();
        var count = 0;
        foreach (var (profileId, scope) in grants)
        {
            if (++count > 64 || profileId == Guid.Empty || scope != ProfileListScope)
                throw new ArgumentException("Invalid metadata grant.", nameof(grants));
            profileIds.Add(profileId);
        }
        _profileIds = profileIds.ToFrozenSet();
    }

    public bool Allows(Guid profileId, string scope) => scope == ProfileListScope && _profileIds.Contains(profileId);
}
