namespace WinShell.App.Features.Mcp;

public enum McpServiceTransport { Stdio, StreamableHttp }
public sealed record McpServiceSettings(McpServiceTransport Transport = McpServiceTransport.Stdio, int Port = 8765, bool EnableLegacySse = false)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Transport)) throw new ArgumentOutOfRangeException(nameof(Transport));
        if (Port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
    }
    public string ConfiguredEndpoint => $"http://127.0.0.1:{Port}/mcp";
}
