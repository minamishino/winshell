using System.Globalization;

namespace WinShell.Mcp.Http;

public sealed record McpLocalHttpOptions(int Port = 8765, bool EnableLegacySse = false, bool StdioLifetime = false)
{
    public static McpLocalHttpOptions Parse(string[] arguments)
    {
        var port = 8765; var legacySse = false; var stdioLifetime = false; var hasPort = false;
        for (var index = 0; index < arguments.Length; ++index)
        {
            switch (arguments[index])
            {
                case "--port" when !hasPort && index + 1 < arguments.Length:
                    if (!int.TryParse(arguments[++index], NumberStyles.None, CultureInfo.InvariantCulture, out port)) throw new ArgumentException("invalid_configuration");
                    hasPort = true; break;
                case "--enable-legacy-sse" when !legacySse: legacySse = true; break;
                case "--stdio-lifetime" when !stdioLifetime: stdioLifetime = true; break;
                default: throw new ArgumentException("invalid_configuration");
            }
        }
        var options = new McpLocalHttpOptions(port, legacySse, stdioLifetime);
        options.Validate(); return options;
    }
    internal void Validate(bool allowEphemeralPort = false)
    {
        if (!(allowEphemeralPort && Port == 0) && Port is < 1024 or > 65535) throw new ArgumentException("invalid_configuration");
    }
}

public enum McpLocalHttpState { Created, Starting, Running, Closing, Closed, Failed }
public sealed record McpLocalHttpStatistics(int ActiveHttpRequests, int ActiveSessions, int HeldMessageLeases,
    int MaximumHttpRequests, int MaximumSessions, int MaximumMessageLeases, int ExpiredSessions, int CapacityStops, int CleanupFailures);

internal sealed record HttpHostLimits
{
    internal bool AllowEphemeralPort { get; init; }
    internal TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(15);
    internal TimeSpan MessageLifetime { get; init; } = TimeSpan.FromSeconds(45);
    internal long StreamBytes { get; init; } = 8 * 1024 * 1024;
    internal Task? SessionDeleteGate { get; init; }
    internal int SessionDeleteFailureStatus { get; init; }
}
