using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WinShell.Core.Mcp;
using WinShell.Core.Docker;
using WinShell.Core.Ssh;

namespace WinShell.Mcp.Tools;

/// <summary>One shared admission budget for every tool/list call, retained through owned session disposal.</summary>
public sealed class McpToolService(McpPermissionReader permissions, Func<McpAuthorizedProfile, ISshSessionFactory> sessionFactory,
    Func<McpAuthorizedProfile, IDockerService>? dockerFactory = null)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private int _active;
    public static Tool ScriptDefinition { get; } = new()
    {
        Name = McpGrant.ScriptScope,
        Description = "Run a non-PTY UTF-8 Shell script using an explicitly authorized saved SSH profile. Max 8192 bytes, 30 seconds; complete bounded stdout/stderr and actual exit status.",
        InputSchema = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{\"profileId\":{\"type\":\"string\",\"format\":\"uuid\"},\"script\":{\"type\":\"string\"}},\"required\":[\"profileId\",\"script\"],\"additionalProperties\":false}")
    };
    public async ValueTask<ListToolsResult> ListAsync(CancellationToken cancellationToken)
    {
        if (!Enter()) throw new McpException("busy");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            List<Tool> tools = [];
            var scopes = await permissions.ReadAvailableScopesAsync(timeout.Token).ConfigureAwait(false);
            if (scopes.Contains(McpGrant.ProfileListScope)) tools.Add(ProfileMetadataTool.Definition);
            if (scopes.Contains(McpGrant.ScriptScope)) tools.Add(ScriptDefinition);
            tools.AddRange(DockerToolDefinitions.All.Where(tool => scopes.Contains(DockerToolDefinitions.Scope(tool.Name)!)));
            return new() { Tools = tools };
        }
        catch (Exception error) when (FailureCode(error) is not null) { throw new McpException(FailureCode(error)!); }
        finally { Interlocked.Decrement(ref _active); }
    }
    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams? parameters, CancellationToken cancellationToken)
    {
        var metadata = parameters?.Name == McpGrant.ProfileListScope;
        var dockerScope = DockerToolDefinitions.Scope(parameters?.Name);
        Guid profileId = default; string? script = null, containerId = null, command = null;
        if (dockerScope is not null && !DockerToolDefinitions.Arguments(parameters!, out profileId, out containerId, out command))
            return DockerToolResult.Failure("invalid_arguments", parameters!.Name, profileId, null);
        if (dockerScope is null && (metadata ? parameters!.Arguments is { Count: > 0 } : !Arguments(parameters, out profileId, out script))) return Error("invalid_arguments");
        if (!Enter()) return dockerScope is null ? Error("busy") : DockerToolResult.Failure("busy", parameters!.Name, profileId, containerId);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var authorized = await permissions.ReadAsync(dockerScope ?? (metadata ? McpGrant.ProfileListScope : McpGrant.ScriptScope), timeout.Token).ConfigureAwait(false);
            if (metadata) return Text(JsonSerializer.Serialize(authorized.Select(item => new { id = item.Profile.Id, displayName = item.Profile.DisplayName, protocol = "ssh" }), JsonOptions));
            var selected = authorized.SingleOrDefault(item => item.Profile.Id == profileId);
            if (selected is null) return dockerScope is null ? Error("permission_denied") : DockerToolResult.Failure("permission_denied", parameters!.Name, profileId, containerId);
            if (dockerScope is not null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (dockerFactory is null) return DockerToolResult.Failure("unsupported_capability", parameters!.Name, profileId, containerId);
                var docker = dockerFactory(selected);
                var dockerResult = await (parameters!.Name switch
                {
                    "docker.list_containers" => docker.ListContainersAsync(selected.Profile, timeout.Token),
                    "docker.logs" => docker.GetContainerLogsAsync(selected.Profile, containerId!, timeout.Token),
                    "docker.exec_once" => docker.RunContainerCommandAsync(selected.Profile, containerId!, command!, timeout.Token),
                    _ => docker.ChangeContainerStateAsync(selected.Profile, containerId!, DockerToolDefinitions.Action(parameters.Name), timeout.Token)
                }).ConfigureAwait(false);
                return DockerToolResult.From(dockerResult, parameters.Name, profileId, containerId);
            }
            timeout.Token.ThrowIfCancellationRequested();
            var connected = await sessionFactory(selected).ConnectAsync(selected.Profile, timeout.Token).ConfigureAwait(false);
            if (connected is not SshConnectResult.Connected connection) return Error("host_key_untrusted");
            await using var ownedSession = connection.Session;
            if (!connection.Key.Equals(selected.HostKey)) return Error("host_key_changed");
            if (ownedSession is not ISshScriptSession runner) return Error("unsupported_capability");
            var result = await runner.RunScriptAsync(script!, timeout.Token).ConfigureAwait(false);
            if (result.StandardOutput.Length > 32768 || result.StandardError.Length > 16384 ||
                StrictUtf8.GetByteCount(result.StandardOutput) > 32768 || StrictUtf8.GetByteCount(result.StandardError) > 16384 || result.ExitSignal is { Length: > 128 })
                return Error("output_limit");
            return Text(JsonSerializer.Serialize(result, JsonOptions));
        }
        catch (DockerException error) when (dockerScope is not null)
        {
            var code = error.Kind switch { DockerFailureKind.Unconfirmed => "result_unconfirmed", DockerFailureKind.Capacity => "output_limit",
                DockerFailureKind.InvalidInput => "invalid_arguments", DockerFailureKind.InvalidMetadata => "invalid_output", _ => "unsupported_capability" };
            return DockerToolResult.Failure(code, parameters!.Name, profileId, containerId,
                code == "result_unconfirmed" ? (error.InnerException is null ? "missing_result" : FailureCode(error.InnerException) ?? "remote_failure") : null);
        }
        catch (Exception error) when (FailureCode(error) is not null)
        { return dockerScope is null ? Error(FailureCode(error)!) : DockerToolResult.Failure(FailureCode(error)!, parameters!.Name, profileId, containerId); }
        finally { Interlocked.Decrement(ref _active); }
    }
    private bool Enter()
    {
        if (Interlocked.Increment(ref _active) <= 4) return true;
        Interlocked.Decrement(ref _active); return false;
    }
    private static bool Arguments(CallToolRequestParams? parameters, out Guid profileId, out string? script)
    {
        profileId = default; script = null;
        if (parameters?.Name != McpGrant.ScriptScope || parameters.Arguments is not { Count: 2 } arguments ||
            !arguments.TryGetValue("profileId", out var id) || id.ValueKind != JsonValueKind.String ||
            !arguments.TryGetValue("script", out var body) || body.ValueKind != JsonValueKind.String) return false;
        try
        {
            var text = id.GetString();
            if (!Guid.TryParseExact(text, "D", out profileId) || profileId == Guid.Empty || profileId.ToString("D") != text) return false;
            script = body.GetString();
            return script is not null && script.Length <= 8192 && !script.Contains('\0') && StrictUtf8.GetByteCount(script) <= 8192;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
    }
    private static string? FailureCode(Exception error) => error switch
    {
        OperationCanceledException => "cancelled",
        TimeoutException => "storage_busy",
        UnauthorizedAccessException => "permission_denied",
        InvalidDataException or NotSupportedException => "storage_invalid",
        IOException => "storage_unavailable",
        EncoderFallbackException => "invalid_output",
        SshConnectionException ssh => ssh.Kind switch
        {
            SshFailureKind.Authentication => "authentication_failed", SshFailureKind.HostKeyChanged => "host_key_changed",
            SshFailureKind.MissingCredential or SshFailureKind.CredentialAccess or SshFailureKind.PrivateKey => "credential_unavailable",
            SshFailureKind.Network => "network_failure", SshFailureKind.Permission => "permission_denied",
            SshFailureKind.Timeout => "timeout", SshFailureKind.Capacity => "output_limit", SshFailureKind.Storage => "storage_unavailable",
            _ => "ssh_failure"
        },
        _ => null
    };
    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };
    private static CallToolResult Error(string code) => new() { IsError = true, Content = [new TextContentBlock { Text = code }] };
}
