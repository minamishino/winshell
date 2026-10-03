namespace WinShell.Core.Mcp;

public interface IMcpGrantRepository
{
    Task<IReadOnlyList<McpGrant>> ReadAsync(CancellationToken cancellationToken);
    /// <summary>Explicitly replaces the complete grant set; empty revokes all. No backup recovery.</summary>
    Task ReplaceAsync(IReadOnlyList<McpGrant> grants, CancellationToken cancellationToken);
}
