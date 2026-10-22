using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinShell.Core.Profiles;
using WinShell.Mcp.Authorization;
using WinShell.Mcp.Tools;
using WinShell.Mcp.Transport;

namespace WinShell.Mcp;

/// <summary>One process/session owns the SDK, transport, and its lazy profile repository.</summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private readonly BoundedStdioTransport _transport;
    private readonly McpServer _server;
    private readonly Lock _gate = new();
    private Task? _runTask;
    private Task? _closeTask;
    public TransportStopReason StopReason { get { lock (_gate) return _transport.StopReason; } }
    public TransportStatistics Statistics { get { lock (_gate) return _transport.Statistics; } }

    public McpServerHost(Stream input, Stream output, McpGrantSnapshot grants, Func<IConnectionProfileRepository> repositoryFactory)
        : this(input, output, MetadataHandlers(grants, repositoryFactory)) { }

    public McpServerHost(Stream input, Stream output, McpToolService tools)
        : this(input, output, new McpServerHandlers
        {
            ListToolsHandler = (_, token) => tools.ListAsync(token),
            CallToolHandler = (request, token) => tools.CallAsync(request.Params, token)
        }) { }

    private static McpServerHandlers MetadataHandlers(McpGrantSnapshot grants, Func<IConnectionProfileRepository> repositoryFactory)
    {
        var metadata = new ProfileMetadataTool(grants, repositoryFactory);
        return new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [ProfileMetadataTool.Definition] }),
            CallToolHandler = (request, token) => metadata.CallAsync(request.Params, token)
        };
    }
    private McpServerHost(Stream input, Stream output, McpServerHandlers handlers)
    {
        _transport = new BoundedStdioTransport(input, output);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "WinShell", Version = "0.1.0" },
            ProtocolVersion = "2025-11-25",
            InitializationTimeout = TimeSpan.FromSeconds(30),
            ScopeRequests = false,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = handlers
        };
        options.Filters.Message.IncomingFilters.Add(_transport.TrackIncoming);
        _server = McpServer.Create(_transport, options, loggerFactory: null);
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closeTask is not null, this);
            return _runTask ??= RunCoreAsync(cancellationToken);
        }
    }
    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => _transport.Stop(TransportStopReason.Cancelled));
        try { await _server.RunAsync(_transport.Stopping).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_transport.Stopping.IsCancellationRequested) { }
        catch { _transport.Stop(TransportStopReason.HandlerFailure); }
        finally { _transport.Stop(TransportStopReason.Cancelled); }
    }
    public Task CloseAsync()
    {
        lock (_gate) return _closeTask ??= CloseCoreAsync();
    }
    private async Task CloseCoreAsync()
    {
        _transport.Stop(TransportStopReason.Cancelled);
        try { if (_runTask is { } run) await run.ConfigureAwait(false); }
        finally
        {
            try { await _server.DisposeAsync().ConfigureAwait(false); }
            finally { await _transport.DisposeAsync().ConfigureAwait(false); }
        }
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
}
