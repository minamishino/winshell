using System.Text.Json;
using ModelContextProtocol.Protocol;
using WinShell.Core.Profiles;
using WinShell.Mcp.Authorization;

namespace WinShell.Mcp.Tools;

public sealed class ProfileMetadataTool(McpGrantSnapshot grants, Func<IConnectionProfileRepository> repositoryFactory)
{
    private readonly Lazy<IConnectionProfileRepository> _repository = new(repositoryFactory);
    private int _activeCalls;

    public static Tool Definition { get; } = new()
    {
        Name = McpGrantSnapshot.ProfileListScope,
        Description = "List explicitly authorized SSH profile metadata (id, displayName, protocol).",
        InputSchema = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")
    };

    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.Name != McpGrantSnapshot.ProfileListScope || parameters.Arguments is { Count: > 0 })
            return Error("invalid_arguments");
        if (Interlocked.Increment(ref _activeCalls) > 4)
        {
            Interlocked.Decrement(ref _activeCalls);
            return Error("busy");
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (grants.Count == 0) return Text("[]");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var profiles = await _repository.Value.ReadAsync(timeout.Token).ConfigureAwait(false);
            var metadata = new List<ProfileMetadata>(grants.Count);
            var seenIds = new HashSet<Guid>();
            foreach (var profile in profiles.Profiles)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (profile.Protocol != ConnectionProtocol.Ssh || !grants.Allows(profile.Id, McpGrantSnapshot.ProfileListScope)) continue;
                if (profile.DisplayName is null || profile.DisplayName.Length > 256 || !seenIds.Add(profile.Id) || metadata.Count == 64)
                    return Error("metadata_limit");
                metadata.Add(new(profile.Id, profile.DisplayName, "ssh"));
            }
            // At most 64 x (256 UTF-16 chars escaped to <= 6 bytes + fixed fields), before SDK SerializeToNode.
            return Text(JsonSerializer.Serialize(metadata));
        }
        catch (OperationCanceledException) { return Error("cancelled"); }
        catch (UnauthorizedAccessException) { return Error("permission_denied"); }
        catch (IOException) { return Error("storage_unavailable"); }
        finally { Interlocked.Decrement(ref _activeCalls); }
    }

    private sealed record ProfileMetadata(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] Guid Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("displayName")] string DisplayName,
        [property: System.Text.Json.Serialization.JsonPropertyName("protocol")] string Protocol);
    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };
    private static CallToolResult Error(string code) => new() { IsError = true, Content = [new TextContentBlock { Text = code }] };
}
