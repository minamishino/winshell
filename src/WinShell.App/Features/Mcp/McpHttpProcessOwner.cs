using System.Diagnostics;
using System.Globalization;
using System.Text;
namespace WinShell.App.Features.Mcp;
public enum McpHttpProcessState { Stopped, Starting, Running, Stopping, Failed }
public sealed record McpHttpProcessSnapshot(McpHttpProcessState State, string? Endpoint = null, int? ProcessId = null, string? FailureCode = null, bool Forced = false, bool Joined = true);
public sealed class McpHttpProcessOwner(Func<McpServiceSettings, ProcessStartInfo> createStartInfo, TimeSpan? startDeadline = null, TimeSpan? stopDeadline = null) : IAsyncDisposable
{
    private sealed class Session(Process process, McpServiceSettings settings)
    {
        internal readonly Process Process = process;
        internal readonly McpServiceSettings Settings = settings;
        internal readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Exit = Task.CompletedTask, Output = Task.CompletedTask, Error = Task.CompletedTask, Join = Task.CompletedTask;
        internal Task? Close;
        internal int ProcessId, Bytes, Lines;
        internal bool SeenReady, Stopping, Forced, Joined;
        internal string? Failure;
    }
    private readonly Lock _sync = new();
    private Session? _session;
    private McpHttpProcessState _state;
    private string? _endpoint;
    private bool _disposed;
    private Task? _dispose;
    public event Action? Changed;
    public McpHttpProcessSnapshot Snapshot
    {
        get { lock (_sync) return new McpHttpProcessSnapshot(_state, _endpoint, _session?.ProcessId, _session?.Failure, _session?.Forced ?? false, _session?.Joined ?? true); }
    }
    public static ProcessStartInfo DeploymentStartInfo(string applicationDirectory, McpServiceSettings settings)
    {
        settings.Validate();
        var start = new ProcessStartInfo(Path.Combine(applicationDirectory, "McpHttp", "WinShell.Mcp.Http.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.Combine(applicationDirectory, "McpHttp")
        };
        start.ArgumentList.Add("--port"); start.ArgumentList.Add(settings.Port.ToString(CultureInfo.InvariantCulture));
        if (settings.EnableLegacySse) start.ArgumentList.Add("--enable-legacy-sse");
        start.ArgumentList.Add("--stdio-lifetime"); return start;
    }
    public async Task StartAsync(McpServiceSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        Session session;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is McpHttpProcessState.Starting or McpHttpProcessState.Running or McpHttpProcessState.Stopping) return;
            if (_session is { Joined: false }) throw new InvalidOperationException("previous_child_not_joined");
            var start = createStartInfo(settings);
            if (start.UseShellExecute || !start.RedirectStandardInput || !start.RedirectStandardOutput || !start.RedirectStandardError)
                throw new ArgumentException("owned_pipe_configuration_required");
            session = new Session(new Process { StartInfo = start }, settings); _session = session; _endpoint = null; _state = McpHttpProcessState.Starting;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!session.Process.Start()) throw new IOException("child_start_failed");
                session.ProcessId = session.Process.Id;
                session.Exit = session.Process.WaitForExitAsync(CancellationToken.None);
                // Readers cannot call back through _sync before all owned join tasks are recorded.
                // Startup cancellation must not skip pipe draining or the owned process join.
                var output = session.Process.StandardOutput.BaseStream;
                var errorOutput = session.Process.StandardError.BaseStream;
                session.Output = Task.Run(() => ReadAsync(session, output, false), CancellationToken.None);
                session.Error = Task.Run(() => ReadAsync(session, errorOutput, true), CancellationToken.None);
                session.Join = Task.Run(() => JoinAsync(session), CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                session.Failure = error is OperationCanceledException ? "START_CANCELED" : "CHILD_START_FAILED";
                session.Joined = true; session.Process.Dispose(); _state = McpHttpProcessState.Failed; session.Ready.TrySetResult(false);
            }
        }
        Changed?.Invoke();
        try
        {
            var ready = await session.Ready.Task.WaitAsync(startDeadline ?? TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (!ready) { await CloseSession(session).ConfigureAwait(false); return; }
            Task? close = null;
            lock (_sync)
            {
                if (session.Stopping) close = CloseSession(session);
                else if (session.Failure is null && !session.Exit.IsCompleted)
                { _state = McpHttpProcessState.Running; _endpoint = settings.ConfiguredEndpoint; }
                else { session.Failure ??= "CHILD_EXITED"; close = CloseSession(session); }
            }
            if (close is not null) await close.ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            Fail(session, error is TimeoutException ? "READY_TIMEOUT" : "START_CANCELED");
            await CloseSession(session).ConfigureAwait(false);
        }
        Changed?.Invoke();
    }
    private async Task ReadAsync(Session session, Stream input, bool errorStream)
    {
        var buffer = new byte[1024]; var line = new byte[256]; var length = 0;
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                if (Interlocked.Add(ref session.Bytes, count) > 65536) { Fail(session, "OUTPUT_LIMIT"); return; }
                foreach (var value in buffer.AsSpan(0, count))
                {
                    if (value == (byte)'\n')
                    {
                        if (Interlocked.Increment(ref session.Lines) > 128) { Fail(session, "OUTPUT_LIMIT"); return; }
                        var size = length > 0 && line[length - 1] == (byte)'\r' ? length - 1 : length;
                        if (!AcceptLine(session, Encoding.ASCII.GetString(line, 0, size), errorStream)) return;
                        length = 0;
                    }
                    else
                    {
                        if (length == line.Length || value > 127) { Fail(session, "OUTPUT_LIMIT"); return; }
                        line[length++] = value;
                    }
                }
            }
            if (length != 0) Fail(session, "INVALID_READY");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { if (!session.Stopping) Fail(session, "PIPE_FAILED"); }
    }
    private bool AcceptLine(Session session, string line, bool errorStream)
    {
        if (errorStream)
        {
            if (line == "MCP_HTTP_STOPPED") return true;
            Fail(session, line is "MCP_HTTP_TOKEN_MISSING" or "MCP_HTTP_ALREADY_RUNNING" or "MCP_HTTP_INVALID_CONFIGURATION" or "MCP_HTTP_STARTUP_OR_SHUTDOWN_FAILED"
                ? line : "INVALID_DIAGNOSTIC");
            return false;
        }
        var expected = $"MCP_HTTP_READY port={session.Settings.Port.ToString(CultureInfo.InvariantCulture)} legacy_sse={(session.Settings.EnableLegacySse ? 1 : 0)}";
        lock (_sync)
        {
            if (line == expected && session is { SeenReady: false, Stopping: false })
            { session.SeenReady = true; session.Ready.TrySetResult(true); return true; }
        }
        Fail(session, "INVALID_READY"); return false;
    }
    private void Fail(Session session, string code)
    {
        lock (_sync) { session.Failure ??= code; session.Ready.TrySetResult(false); }
        _ = CloseSession(session); // Tracked per-session Close; readers must never await their own Join.
    }
    public Task StopAsync()
    { lock (_sync) return _session is { } session ? CloseSession(session) : Task.CompletedTask; }
    private Task CloseSession(Session session)
    {
        lock (_sync) return session.Close ??= CloseCoreAsync(session);
    }
    private async Task CloseCoreAsync(Session session)
    {
        session.Stopping = true;
        if (!session.Joined) { _state = McpHttpProcessState.Stopping; _endpoint = null; }
        session.Ready.TrySetResult(false);
        await Task.Yield(); // Caller holds _sync; do not publish callbacks under it.
        Changed?.Invoke();
        if (session.Joined) return;
        try { session.Process.StandardInput.Close(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        try { await session.Exit.WaitAsync(stopDeadline ?? TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            session.Forced = true;
            if (!session.Exit.IsCompleted) session.Process.Kill(entireProcessTree: true);
            await session.Exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        await session.Join.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Changed?.Invoke();
    }
    private async Task JoinAsync(Session session)
    {
        await session.Exit.ConfigureAwait(false);
        await Task.WhenAll(session.Output, session.Error).ConfigureAwait(false);
        lock (_sync)
        {
            if (!session.Forced && session.Process.ExitCode != 0) session.Failure ??= "CHILD_FAILED";
            if (!session.Stopping) session.Failure ??= "CHILD_EXITED";
            session.Joined = true; _endpoint = null;
            _state = session.Failure is null ? McpHttpProcessState.Stopped : McpHttpProcessState.Failed;
            session.Ready.TrySetResult(false);
            session.Process.Dispose();
        }
        Changed?.Invoke();
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            return new ValueTask(_dispose ??= StopAsync());
        }
    }
}
