namespace WinShell.Core.Tunnels;
public interface ITunnelDefinitionRepository
{
    Task<IReadOnlyList<TunnelDefinition>> ReadAsync(CancellationToken cancellationToken);
    Task ReplaceAsync(IReadOnlyList<TunnelDefinition> definitions, CancellationToken cancellationToken);
}
