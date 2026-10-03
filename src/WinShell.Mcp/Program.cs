using WinShell.Infrastructure.Windows.Profiles;
using WinShell.Mcp;
using WinShell.Mcp.Authorization;
using WinShell.Mcp.Transport;

// No grant loader or writer exists yet. Empty authorization does not invoke this lazy factory.
try
{
    await using var input = StandardPipeStreams.OpenInput();
    await using var output = StandardPipeStreams.OpenOutput();
    await using var host = new McpServerHost(input, output,
        McpGrantSnapshot.Empty, () => new JsonConnectionProfileRepository(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShell")));
    await host.RunAsync();
    await host.CloseAsync();
    await WriteDiagnosticAsync($"MCP_STOP code={(int)host.StopReason} leases={host.Statistics.MaximumHeldLeases} sends={host.Statistics.MaximumActiveSends}\n");
    return host.StopReason is TransportStopReason.EndOfInput or TransportStopReason.Cancelled ? 0 : 1;
}
catch
{
    // One fixed, bounded event; never write exception text, request data, paths, or identifiers.
    await WriteDiagnosticAsync("MCP_STARTUP_FAILED\n");
    return 1;
}

// At most two fixed-code lines per process, each below 128 UTF8 bytes (<1000 rows / 64KiB).
static async Task WriteDiagnosticAsync(string diagnostic)
{
    try
    {
        await using var error = StandardPipeStreams.OpenError();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await error.WriteAsync(System.Text.Encoding.UTF8.GetBytes(diagnostic), deadline.Token);
    }
    catch { /* A closed diagnostic pipe is reported by the process exit status. */ }
}
