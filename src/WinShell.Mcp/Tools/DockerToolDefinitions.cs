using System.Text.Json;
using ModelContextProtocol.Protocol;
using WinShell.Core.Docker;
using WinShell.Core.Mcp;
using WinShell.Infrastructure.Ssh.Docker;
namespace WinShell.Mcp.Tools;

internal static class DockerToolDefinitions
{
    internal static IReadOnlyList<Tool> All { get; } =
    [
        Define("list_containers", "List at most 32 containers.", false, false),
        Define("logs", "Read the last 200 log lines; logs may contain secrets.", true, false),
        Define("start", "Start a container. Never automatically retry an unconfirmed result.", true, false),
        Define("stop", "Stop a container with a 10 second grace period. Never automatically retry an unconfirmed result.", true, false),
        Define("restart", "Restart a container with a 10 second grace period. Never automatically retry an unconfirmed result.", true, false),
        Define("exec_once", "Execute one arbitrary /bin/sh command without PTY; at most 4096 UTF-8 bytes and 30 seconds. Never automatically retry an unconfirmed result.", true, true)
    ];
    internal static string? Scope(string? name) => name switch
    {
        "docker.list_containers" or "docker.logs" => McpGrant.DockerReadScope,
        "docker.start" or "docker.stop" or "docker.restart" => McpGrant.DockerControlScope,
        "docker.exec_once" => McpGrant.DockerExecScope,
        _ => null
    };
    internal static bool Mutation(string name) => Scope(name) is McpGrant.DockerControlScope or McpGrant.DockerExecScope;
    internal static DockerContainerAction Action(string name) => name switch
    {
        "docker.start" => DockerContainerAction.Start,
        "docker.stop" => DockerContainerAction.Stop,
        "docker.restart" => DockerContainerAction.Restart,
        _ => throw new ArgumentException("Invalid action.")
    };
    internal static bool Arguments(CallToolRequestParams parameters, out Guid profileId, out string? containerId, out string? command)
    {
        profileId = Guid.Empty; containerId = null; command = null;
        var container = parameters.Name != "docker.list_containers";
        var execute = parameters.Name == "docker.exec_once";
        if (parameters.Arguments is not { } arguments || arguments.Count != 1 + (container ? 1 : 0) + (execute ? 1 : 0) ||
            !arguments.TryGetValue("profileId", out var id) || id.ValueKind != JsonValueKind.String) return false;
        try
        {
            var text = id.GetString();
            if (!Guid.TryParseExact(text, "D", out profileId) || profileId == Guid.Empty || profileId.ToString("D") != text) return false;
            if (container)
            {
                if (!arguments.TryGetValue("containerId", out var value) || value.ValueKind != JsonValueKind.String) return false;
                containerId = value.GetString();
            }
            if (execute)
            {
                if (!arguments.TryGetValue("command", out var value) || value.ValueKind != JsonValueKind.String) return false;
                command = value.GetString();
            }
            // Use the production builder to check the complete quoted outer script before reading any stores.
            _ = parameters.Name switch
            {
                "docker.list_containers" => DockerScriptBuilder.List(),
                "docker.logs" => DockerScriptBuilder.Logs(containerId!),
                "docker.exec_once" => DockerScriptBuilder.Command(containerId!, command!),
                _ => DockerScriptBuilder.Change(containerId!, Action(parameters.Name))
            };
            return true;
        }
        catch (Exception error) when (error is DockerException or ArgumentException or InvalidOperationException) { return false; }
    }
    private static Tool Define(string action, string description, bool container, bool command)
    {
        var properties = new Dictionary<string, object>
        {
            ["profileId"] = new { type = "string", format = "uuid", pattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$" }
        };
        var required = new List<string> { "profileId" };
        if (container) { properties["containerId"] = new { type = "string", pattern = "^[0-9a-f]{64}$" }; required.Add("containerId"); }
        if (command) { properties["command"] = new { type = "string", maxLength = 4096 }; required.Add("command"); }
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false });
        return new Tool { Name = "docker." + action, Description = description + " Requires saved native authorization, SSH/Linux POSIX shell and Docker CLI access to unix:///var/run/docker.sock; applies to all containers on the authorized host.",
            InputSchema = schema };
    }
}
