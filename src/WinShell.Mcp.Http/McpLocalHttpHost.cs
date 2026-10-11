using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WinShell.Core.Credentials;
using WinShell.Mcp.Tools;

namespace WinShell.Mcp.Http;

/// <summary>One loopback host owns all SDK sessions, admission, and cleanup. No WinUI dependency.
/// Authentication uses a startup snapshot; stop/join before token rotation and restart afterward.</summary>
public sealed class McpLocalHttpHost : IAsyncDisposable
{
    private const int MaximumMessageBytes = 512 * 1024;
    private readonly object _gate = new();
    private readonly McpLocalHttpOptions _options;
    private readonly HttpHostLimits _limits;
    private readonly McpServerHandlers _handlers;
    private readonly byte[] _authorizationHash;
    private readonly byte[] _authorizationBytes;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<SessionOwner> _sessions = [];
    private readonly ConcurrentDictionary<string, SessionOwner> _statefulSessions = new(StringComparer.Ordinal);
    private readonly HashSet<HttpContext> _requests = [];
    private readonly HashSet<Task> _cleanupTasks = [];
    private WebApplication? _application;
    private Task? _startTask, _closeTask;
    private int _ordinaryLeases, _controlLeases, _maximumHttp, _maximumSessions, _maximumLeases, _expired, _capacityStops, _cleanupFailures;
    private McpLocalHttpState _state;
    public Uri? Endpoint { get; private set; }
    public Uri? LegacySseEndpoint => _options.EnableLegacySse && Endpoint is not null ? new Uri(Endpoint.AbsoluteUri + "/sse") : null;
    public McpLocalHttpState State { get { lock (_gate) return _state; } }
    public McpLocalHttpStatistics Statistics
    {
        get { lock (_gate) return new(_requests.Count, _sessions.Count, _ordinaryLeases + _controlLeases,
            _maximumHttp, _maximumSessions, _maximumLeases, _expired, _capacityStops, _cleanupFailures); }
    }
    public McpLocalHttpHost(McpLocalHttpOptions options, McpToolService tools, CredentialSecret secret)
        : this(options, tools, secret, new HttpHostLimits()) { }
    internal McpLocalHttpHost(McpLocalHttpOptions options, McpToolService tools, CredentialSecret secret, HttpHostLimits limits)
        : this(options, new McpServerHandlers { ListToolsHandler = (_, token) => tools.ListAsync(token), CallToolHandler = (request, token) => tools.CallAsync(request.Params, token) }, secret, limits) { }
    internal McpLocalHttpHost(McpLocalHttpOptions options, McpServerHandlers handlers, CredentialSecret secret, HttpHostLimits limits)
    {
        options.Validate(limits.AllowEphemeralPort);
        if (secret.Bytes.Length != 32) throw new ArgumentException("invalid_token");
        _options = options; _limits = limits; _handlers = handlers;
        _authorizationBytes = Encoding.ASCII.GetBytes("Bearer " + Convert.ToBase64String(secret.Bytes.Span));
        _authorizationHash = SHA256.HashData(_authorizationBytes);
    }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closeTask is not null) throw new ObjectDisposedException(nameof(McpLocalHttpHost));
            return _startTask ??= StartCoreAsync(cancellationToken);
        }
    }
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        _state = McpLocalHttpState.Starting;
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, _options.Port, listener => listener.Protocols = HttpProtocols.Http1);
            kestrel.Limits.MaxRequestBodySize = MaximumMessageBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = 8192;
            kestrel.Limits.MaxRequestHeaderCount = 32;
            kestrel.Limits.MaxConcurrentConnections = 64;
            kestrel.Limits.MaxConcurrentUpgradedConnections = 0;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Services.AddMcpServer(server =>
        {
            server.ServerInfo = new Implementation { Name = "WinShell", Version = "0.1.0" };
            server.ProtocolVersion = "2025-11-25";
            server.InitializationTimeout = TimeSpan.FromSeconds(30);
            server.ScopeRequests = false;
            server.Capabilities = new ServerCapabilities { Tools = new ToolsCapability() };
            server.Handlers = _handlers;
        }).WithHttpTransport(ConfigureCompatibilityTransport);
        var application = builder.Build(); _application = application;
        application.Use(BoundRequestAsync);
        application.MapMcp("/mcp");
        try
        {
            await application.StartAsync(starting.Token).ConfigureAwait(false);
            Endpoint = new Uri(application.Urls.Single().TrimEnd('/') + "/mcp");
            lock (_gate) _state = McpLocalHttpState.Running;
        }
        catch { lock (_gate) _state = McpLocalHttpState.Failed; throw; }
    }

    // SDK 2.2.0 experimental lifecycle extension is isolated here. It supplies session tokens,
    // including SSE disconnect/shutdown cancellation. Stateful/legacy APIs are explicit compatibility.
    private void ConfigureCompatibilityTransport(HttpServerTransportOptions transport)
    {
        transport.SessionMode = _options.EnableLegacySse ? HttpServerSessionMode.Stateful : HttpServerSessionMode.Stateless;
#pragma warning disable MCP9004, MCP9006 // Explicit legacy-client compatibility only; covered by real SDK wire tests.
        transport.EnableLegacySse = _options.EnableLegacySse;
        transport.IdleTimeout = TimeSpan.FromMinutes(1);
        transport.MaxIdleSessionCount = 4;
#pragma warning restore MCP9004, MCP9006
        transport.ConfigureSessionOptions = (context, server, token) =>
        {
            SessionOwner owner;
            lock (_gate)
            {
                if (_stop.IsCancellationRequested || _sessions.Count == 4) throw new HttpAdmissionException();
                owner = new SessionOwner(this, context);
                _sessions.Add(owner); _maximumSessions = Math.Max(_maximumSessions, _sessions.Count);
            }
            context.Features.Set(owner);
            server.Filters.Message.IncomingFilters.Add(next => (message, cancellation) => IncomingAsync(owner, next, message, cancellation));
            server.Filters.Message.OutgoingFilters.Add(next => (message, cancellation) => OutgoingAsync(owner, next, message, cancellation));
            return Task.CompletedTask;
        };
#pragma warning disable MCPEXP002 // Official SDK lifecycle hook; no private reflection or protocol implementation.
        transport.RunSessionHandler = (context, server, token) =>
        {
            var owner = context.Features.Get<SessionOwner>() ?? throw new InvalidOperationException("session_missing");
            return owner.RunTask = RunSessionAsync(context, server, token);
        };
#pragma warning restore MCPEXP002
    }

    private async Task RunSessionAsync(HttpContext context, McpServer server, CancellationToken cancellationToken)
    {
        var owner = context.Features.Get<SessionOwner>() ?? throw new InvalidOperationException("session_missing");
        owner.Started = true; owner.SessionId = server.SessionId;
        if (!owner.IsSse && server.SessionId is { } sessionId) _statefulSessions[sessionId] = owner;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Stopping, _stop.Token);
        try { await server.RunAsync(running.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (running.IsCancellationRequested) { }
        finally
        {
            try { await server.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                owner.Stop(false);
                if (!owner.IsSse && owner.SessionId is not null && !_stop.IsCancellationRequested)
                    QueueOfficialSessionDeletion(owner);
                // Stateless/SSE stay admitted through the SDK transport.Dispose and HTTP middleware;
                // shutdown releases remaining owners only after the official host has stopped and joined.
            }
        }
    }
    private void QueueOfficialSessionDeletion(SessionOwner owner)
    {
        // DELETE waits for the SDK RunSession task. Start on a fresh continuation so this callback
        // can return before the endpoint joins it. Lease remains held until official SDK removal finishes.
        var task = Task.Run(async () =>
        {
            await Task.Yield();
            try
            {
                if (owner.RunTask is { } run) await run.ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
                using var request = new HttpRequestMessage(HttpMethod.Delete, Endpoint);
                request.Headers.TryAddWithoutValidation("Authorization", Encoding.ASCII.GetString(_authorizationBytes));
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", owner.SessionId);
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
                using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound) FailCleanup();
                // SDK DELETE removes its dictionary entry before transport disposal. A racing DELETE
                // may return 404 while the first DELETE is still disposing: join both admitted endpoints.
                Task[] deletions; lock (_gate) deletions = [.. owner.DeleteTasks];
                await Task.WhenAll(deletions).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch when (_stop.IsCancellationRequested) { }
            catch { FailCleanup(); }
            finally { if (!_stop.IsCancellationRequested) await ReleaseOwnerAsync(owner).ConfigureAwait(false); }
        });
        lock (_gate) _cleanupTasks.Add(task);
        _ = task.ContinueWith(completed => { lock (_gate) _cleanupTasks.Remove(completed); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void FailCleanup()
    {
        Interlocked.Increment(ref _cleanupFailures);
        // Close is owned by _closeTask. Do not await it from the cleanup task: Close joins
        // that task after stopping and disposing the official SDK host and transports.
        _ = CloseAsync();
    }

    private async Task IncomingAsync(SessionOwner owner, McpMessageHandler next, MessageContext context, CancellationToken cancellationToken)
    {
        var control = context.JsonRpcMessage is JsonRpcResponse or JsonRpcError || context.JsonRpcMessage is JsonRpcNotification { Method: "notifications/cancelled" or "notifications/initialized" };
        lock (_gate)
        {
            if (owner.Stopping.IsCancellationRequested) return;
            if ((control ? _controlLeases >= 4 : _ordinaryLeases >= 12) || _ordinaryLeases + _controlLeases >= 16)
            { ++_capacityStops; owner.Stop(false); return; }
            if (control) { ++_controlLeases; ++owner.ControlLeases; } else { ++_ordinaryLeases; ++owner.OrdinaryLeases; }
            _maximumLeases = Math.Max(_maximumLeases, _ordinaryLeases + _controlLeases);
        }
        using var message = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Stopping);
        message.CancelAfter(_limits.MessageLifetime);
        using var expiration = message.Token.Register(() => owner.Stop(false));
        try
        {
            ValidateMessage(context.JsonRpcMessage);
            await next(context, message.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (control) { --_controlLeases; --owner.ControlLeases; } else { --_ordinaryLeases; --owner.OrdinaryLeases; }
            }
        }
        catch { owner.Stop(false); /* No additional SDK error send after stopping; ownership retained until session disposal. */ }
    }
    private async Task OutgoingAsync(SessionOwner owner, McpMessageHandler next, MessageContext context, CancellationToken cancellationToken)
    {
        if (owner.Stopping.IsCancellationRequested) return;
        try
        {
            ValidateMessage(context.JsonRpcMessage);
            using var writing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Stopping);
            await next(context, writing.Token).ConfigureAwait(false);
        }
        catch { owner.Stop(false); }
    }
    private static void ValidateMessage(JsonRpcMessage message)
    {
        if (message is JsonRpcMessageWithId { Id.Id: string id } && id.Length > 128) throw new InvalidDataException("message_limit");
        if (message is JsonRpcRequest { Method.Length: > 128 } or JsonRpcNotification { Method.Length: > 128 }) throw new InvalidDataException("message_limit");
        using var writer = new Utf8JsonWriter(new HttpCappedBuffer(MaximumMessageBytes), new JsonWriterOptions { MaxDepth = 64 });
        JsonSerializer.Serialize(writer, message, McpJsonUtilities.DefaultOptions);
        writer.Flush();
    }

    private async Task BoundRequestAsync(HttpContext context, RequestDelegate next)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || _requests.Count == 32) { context.Response.StatusCode = 429; return; }
            _requests.Add(context); _maximumHttp = Math.Max(_maximumHttp, _requests.Count);
        }
        var originalOutput = context.Response.Body;
        var originalInput = context.Request.Body;
        MemoryStream? boundedInput = null;
        SessionOwner? deletionOwner = null;
        TaskCompletionSource? deletionJoined = null;
        try
        {
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                context.Request.Host.Value != "127.0.0.1:" + Endpoint?.Port)
            { context.Response.StatusCode = 403; return; }
            var origin = context.Request.Headers.Origin;
            if (origin.Count > 0 && (origin.Count != 1 || origin[0] != Endpoint!.GetLeftPart(UriPartial.Authority)))
            { context.Response.StatusCode = 403; return; }
            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count != 1 || authorization[0] is not { Length: 51 } value ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(value)), _authorizationHash))
            { context.Response.StatusCode = 401; return; }
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "winshell-local-http")], "LocalBearer"));
            if (context.Request.Method == "DELETE")
            {
                lock (_gate)
                {
                    if (_statefulSessions.TryGetValue(context.Request.Headers["Mcp-Session-Id"].ToString(), out deletionOwner))
                    {
                        deletionJoined = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        deletionOwner.DeleteTasks.Add(deletionJoined.Task);
                    }
                }
                if (_limits.SessionDeleteGate is { } deleteGate) await deleteGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                if (_limits.SessionDeleteFailureStatus != 0) { context.Response.StatusCode = _limits.SessionDeleteFailureStatus; return; }
            }
            if (context.Request.Headers["Mcp-Session-Id"].ToString() is { Length: > 0 } sessionId &&
                _statefulSessions.TryGetValue(sessionId, out var existing) && existing.Stopping.IsCancellationRequested && context.Request.Method != "DELETE")
            { context.Response.StatusCode = 404; return; }
            if (context.Request.Method == "POST")
            {
                if (context.Request.ContentLength > MaximumMessageBytes) { context.Response.StatusCode = 413; return; }
                var bytes = new byte[MaximumMessageBytes + 1]; var used = 0;
                using var read = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
                read.CancelAfter(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var count = await context.Request.Body.ReadAsync(bytes.AsMemory(used), read.Token).ConfigureAwait(false);
                    used += count;
                    if (used > MaximumMessageBytes) { context.Response.StatusCode = 413; return; }
                    if (count == 0) break;
                }
                ValidateJson(bytes.AsSpan(0, used));
                boundedInput = new MemoryStream(bytes, 0, used, false);
                context.Request.Body = boundedInput;
            }
            context.Response.Body = new HttpBoundedOutput(originalOutput, _limits.StreamBytes, () =>
            { context.Features.Get<SessionOwner>()?.Stop(false); context.Abort(); });
            await next(context).ConfigureAwait(false);
        }
        catch (HttpAdmissionException) { if (!context.Response.HasStarted) context.Response.StatusCode = 429; else context.Abort(); }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { if (!context.Response.HasStarted) context.Response.StatusCode = error.StatusCode; else context.Abort(); }
        catch (JsonException) { if (!context.Response.HasStarted) context.Response.StatusCode = 400; else context.Abort(); }
        catch (OperationCanceledException) { context.Abort(); }
        catch { if (!context.Response.HasStarted) context.Response.StatusCode = 400; else context.Abort(); }
        finally
        {
            context.Response.Body = originalOutput;
            context.Request.Body = originalInput;
            boundedInput?.Dispose();
            var owner = context.Features.Get<SessionOwner>();
            if (owner is not null && (owner.IsSse || owner.SessionId is null || !owner.Started)) await ReleaseOwnerAsync(owner).ConfigureAwait(false);
            lock (_gate)
            {
                _requests.Remove(context);
                if (deletionJoined is not null)
                {
                    deletionOwner!.DeleteTasks.Remove(deletionJoined.Task);
                    deletionJoined.TrySetResult();
                }
            }
        }
    }
    private static void ValidateJson(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 }); var tokens = 0;
        while (reader.Read()) if (++tokens > 8192) throw new JsonException("message_limit");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
    }
    private async Task ReleaseOwnerAsync(SessionOwner owner)
    {
        await owner.CloseAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (!_sessions.Remove(owner)) return;
            _ordinaryLeases -= owner.OrdinaryLeases; _controlLeases -= owner.ControlLeases;
            owner.OrdinaryLeases = owner.ControlLeases = 0;
            if (owner.SessionId is { } id) _statefulSessions.TryRemove(id, out _);
        }
    }
    public Task CloseAsync()
    {
        lock (_gate) return _closeTask ??= CloseCoreAsync();
    }
    private async Task CloseCoreAsync()
    {
        _stop.Cancel();
        try { if (_startTask is { } start) await start.ConfigureAwait(false); } catch { }
        lock (_gate) { _state = McpLocalHttpState.Closing; foreach (var owner in _sessions) owner.Stop(false); foreach (var request in _requests) request.Abort(); }
        try
        {
            if (_application is { } application)
            {
                await application.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await application.DisposeAsync().ConfigureAwait(false);
            }
            Task[] cleanup; lock (_gate) cleanup = [.. _cleanupTasks];
            await Task.WhenAll(cleanup).ConfigureAwait(false);
            SessionOwner[] owners; lock (_gate) owners = [.. _sessions];
            foreach (var owner in owners) await ReleaseOwnerAsync(owner).ConfigureAwait(false);
            lock (_gate) _state = McpLocalHttpState.Closed;
        }
        catch { lock (_gate) _state = McpLocalHttpState.Failed; throw; }
        finally { CryptographicOperations.ZeroMemory(_authorizationHash); CryptographicOperations.ZeroMemory(_authorizationBytes); _stop.Dispose(); }
    }
    public async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        Task? closing; lock (_gate) closing = _closeTask;
        if (closing is not null) { await closing.ConfigureAwait(false); ThrowIfCleanupFailed(); return; }
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try { await Task.Delay(Timeout.Infinite, waiting.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
        await CloseAsync().ConfigureAwait(false);
        ThrowIfCleanupFailed();
    }
    private void ThrowIfCleanupFailed()
    { if (Volatile.Read(ref _cleanupFailures) != 0) throw new IOException("session_cleanup_failed"); }
    public ValueTask DisposeAsync() => new(CloseAsync());

    private sealed class HttpAdmissionException : Exception;
    private sealed class SessionOwner
    {
        private readonly McpLocalHttpHost _host;
        private readonly HttpContext _context;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Timer _timer;
        private readonly object _closeGate = new();
        private Task? _closeTask;
        private int _stopped;
        internal bool Started;
        internal Task? RunTask;
        internal string? SessionId;
        internal int OrdinaryLeases, ControlLeases;
        internal HashSet<Task> DeleteTasks { get; } = [];
        internal bool IsSse { get; }
        internal CancellationToken Stopping { get; }
        internal SessionOwner(McpLocalHttpHost host, HttpContext context)
        {
            _host = host; _context = context; IsSse = context.Request.Path == "/mcp/sse";
            Stopping = _stopping.Token;
            _timer = new Timer(_ => Stop(true), null, host._limits.SessionLifetime, Timeout.InfiniteTimeSpan);
        }
        internal void Stop(bool expired)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            if (expired) { lock (_host._gate) ++_host._expired; }
            _stopping.Cancel();
            if (IsSse) _context.Abort();
        }
        internal Task CloseAsync()
        { lock (_closeGate) return _closeTask ??= CloseCoreAsync(); }
        private async Task CloseCoreAsync()
        { Stop(false); await _timer.DisposeAsync().ConfigureAwait(false); _stopping.Dispose(); }
    }
}
