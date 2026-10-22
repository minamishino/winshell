using WinShell.Core.Connections;
using WinShell.Core.Mcp;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using WinShell.Infrastructure.Ssh;
using WinShell.Infrastructure.Ssh.Docker;
using WinShell.Infrastructure.Windows.Credentials;
using WinShell.Infrastructure.Windows.Mcp;
using WinShell.Infrastructure.Windows.Profiles;
using WinShell.Infrastructure.Windows.Ssh;
using WinShell.Mcp.Http;
using WinShell.Mcp.Tools;

try
{
    var options = McpLocalHttpOptions.Parse(args);
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShell");
    var tokenStore = new LocalMcpHttpTokenStore(root);
    await using var runningLease = await tokenStore.AcquireRunningLeaseAsync(CancellationToken.None);
    if (runningLease is null) { await Console.Error.WriteLineAsync("MCP_HTTP_TOKEN_MISSING"); return 2; }
    using var secret = await tokenStore.ReadAsync(CancellationToken.None);
    if (secret is null) { await Console.Error.WriteLineAsync("MCP_HTTP_TOKEN_MISSING"); return 2; }
    var permissions = new McpPermissionReader(new JsonMcpGrantRepository(root), new JsonConnectionProfileRepository(root), new JsonKnownHostRepository(root));
    var connections = new ConnectionAdmissionBudget();
    var tools = new McpToolService(permissions,
        authorized => new SshSessionFactory(new HttpGrantedKnownHosts(authorized), new WindowsCredentialStore(), connections),
        authorized => new DockerService(new SshSessionFactory(new HttpGrantedKnownHosts(authorized), new WindowsCredentialStore(), connections), new HttpAuthorizedProfiles(authorized)));
    await using var host = new McpLocalHttpHost(options, tools, secret);
    using var shutdown = new HttpConsoleCancellation();
    await HttpProcessLifetime.RunAsync(host, options, shutdown.Source);
    return 0;
}
catch (ArgumentException) { await Console.Error.WriteLineAsync("MCP_HTTP_INVALID_CONFIGURATION"); return 2; }
catch (IOException error) when (error.Message == "token_store_running") { await Console.Error.WriteLineAsync("MCP_HTTP_ALREADY_RUNNING"); return 2; }
catch { await Console.Error.WriteLineAsync("MCP_HTTP_STARTUP_OR_SHUTDOWN_FAILED"); return 1; }

internal sealed class HttpGrantedKnownHosts(McpAuthorizedProfile authorized) : IKnownHostRepository
{
    public Task<SshHostKey?> ReadAsync(SshEndpointIdentity endpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return endpoint.Equals(new SshEndpointIdentity(authorized.Profile.Host, authorized.Profile.Port))
            ? Task.FromResult<SshHostKey?>(authorized.HostKey)
            : throw new InvalidDataException("unauthorized_endpoint");
    }
    public Task<bool> TrustIfAbsentOrSameAsync(SshEndpointIdentity endpoint, SshHostKey key, CancellationToken token) => throw new NotSupportedException();
}
internal sealed class HttpAuthorizedProfiles(McpAuthorizedProfile authorized) : IConnectionProfileRepository
{
    public Task<ProfileReadResult> ReadAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(new ProfileReadResult([authorized.Profile], false)); }
    public Task UpsertAsync(ConnectionProfile profile, CancellationToken token) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
}

// Unsubscribing alone cannot join an already-dispatched Ctrl+C callback. The same gate
// protects cancellation and source disposal, including callbacks that arrive during close.
internal sealed class HttpConsoleCancellation : IDisposable
{
    private readonly Lock _gate = new();
    private bool _disposed;
    internal CancellationTokenSource Source { get; } = new();

    internal HttpConsoleCancellation() => Console.CancelKeyPress += OnCancelKeyPress;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        lock (_gate)
        {
            eventArgs.Cancel = true;
            if (!_disposed) Source.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Console.CancelKeyPress -= OnCancelKeyPress;
            Source.Dispose();
        }
    }
}
