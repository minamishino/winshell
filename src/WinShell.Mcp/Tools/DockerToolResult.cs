using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using WinShell.Core.Docker;
using WinShell.Mcp.Transport;
namespace WinShell.Mcp.Tools;

internal static partial class DockerToolResult
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private sealed record Payload(string Status, string Action, string ProfileId, string? ContainerId, int? ExitStatus,
        IReadOnlyList<DockerContainerSummary>? Containers = null, string? Stdout = null, string? Stderr = null, string? FirstCause = null, string? Code = null);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Serialization)]
    [JsonSerializable(typeof(Payload))]
    private sealed partial class PayloadJsonContext : JsonSerializerContext;

    internal static CallToolResult Failure(string status, string action, Guid profileId, string? containerId, string? firstCause = null) =>
        Encode(new Payload(status, action, profileId.ToString("D"), containerId, null, FirstCause: firstCause), true);

    internal static CallToolResult From(DockerResult result, string action, Guid profileId, string? containerId)
    {
        if (result.HostKeyChallenge is not null) return Failure("host_key_untrusted", action, profileId, containerId);
        if (result.ExitStatus is null) return Failure(DockerToolDefinitions.Mutation(action) ? "result_unconfirmed" : "invalid_output", action, profileId, containerId, "missing_exit_status");
        var status = result.ExitStatus == 0 ? "completed" : "remote_failed";
        try
        {
            if (result.Containers.Count > 32 || StrictUtf8.GetByteCount(result.StandardOutput) > 32768 || StrictUtf8.GetByteCount(result.StandardError) > 16384)
                throw new InvalidDataException("Output limit.");
            return Encode(new Payload(status, action, profileId.ToString("D"), containerId, result.ExitStatus,
                result.Containers, result.StandardOutput, result.StandardError), result.ExitStatus != 0);
        }
        catch (Exception error) when (error is InvalidDataException or EncoderFallbackException or ArgumentException)
        {
            if (!DockerToolDefinitions.Mutation(action)) return Failure("output_limit", action, profileId, containerId);
            return Encode(new Payload(result.ExitStatus == 0 ? "completed_output_limited" : "remote_failed_output_limited",
                action, profileId.ToString("D"), containerId, result.ExitStatus), result.ExitStatus != 0);
        }
    }
    private static CallToolResult Encode(Payload payload, bool error)
    {
        var staging = new CappedBufferWriter(128 * 1024);
        using (var writer = new Utf8JsonWriter(staging)) JsonSerializer.Serialize(writer, payload with { Code = error ? payload.Status : null }, PayloadJsonContext.Default.Payload);
        return new CallToolResult { IsError = error, Content = [new TextContentBlock { Text = StrictUtf8.GetString(staging.WrittenMemory.Span) }] };
    }
}
