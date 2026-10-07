using WinShell.Infrastructure.Windows.Profiles;
using WinShell.Mcp;
using WinShell.Core.Mcp;
using WinShell.Core.Ssh;
using WinShell.Infrastructure.Ssh;
using WinShell.Infrastructure.Windows.Credentials;
using WinShell.Infrastructure.Windows.Ssh;
using WinShell.Infrastructure.Windows.Mcp;
using WinShell.Mcp.Tools;
using WinShell.Mcp.Transport;

// Only the native permissions page writes grants. Each request reads fresh profile/key-bound scopes.
try
{
    await using var input = StandardPipeStreams.OpenInput();
    await using var output = StandardPipeStreams.OpenOutput();
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShell");
    var reader = new McpPermissionReader(new JsonMcpGrantRepository(root), new JsonConnectionProfileRepository(root), new JsonKnownHostRepository(root));
    var tools = new McpToolService(reader, authorized => new SshSessionFactory(new GrantedKnownHosts(authorized), new WindowsCredentialStore()));
    await using var host = new McpServerHost(input, output, tools);
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

// Pin the key authorized for this call. A concurrent known-host edit cannot substitute a different key before authentication.
internal sealed class GrantedKnownHosts(McpAuthorizedProfile authorized) : IKnownHostRepository
{
    public Task<SshHostKey?> ReadAsync(SshEndpointIdentity endpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!endpoint.Equals(new SshEndpointIdentity(authorized.Profile.Host, authorized.Profile.Port))) throw new InvalidDataException("Unauthorized endpoint.");
        return Task.FromResult<SshHostKey?>(authorized.HostKey);
    }
    public Task<bool> TrustIfAbsentOrSameAsync(SshEndpointIdentity endpoint, SshHostKey key, CancellationToken token) => throw new NotSupportedException();
}
