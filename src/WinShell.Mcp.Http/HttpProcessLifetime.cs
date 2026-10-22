using System.Runtime.ExceptionServices;
using WinShell.Mcp.Transport;

namespace WinShell.Mcp.Http;

/// <summary>Shared production/isolated-fixture process lifetime. Stdout carries one bounded ready event.</summary>
internal static class HttpProcessLifetime
{
    internal static async Task RunAsync(McpLocalHttpHost host, McpLocalHttpOptions options, CancellationTokenSource shutdown)
    {
        await host.StartAsync(shutdown.Token).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"MCP_HTTP_READY port={host.Endpoint!.Port} legacy_sse={(options.EnableLegacySse ? 1 : 0)}");
        await Console.Out.FlushAsync();
        Task? inputLifetime = null;
        Stream? input = null;
        var failures = new List<Exception>();
        try
        {
            if (options.StdioLifetime)
            {
                input = StandardPipeStreams.OpenInput();
                inputLifetime = StopOnInputAsync(input, shutdown);
            }
            await host.WaitForShutdownAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            // Every owned resource must be joined or released even if an earlier cleanup
            // step fails. Keep the original failure first and report after all attempts.
            try { shutdown.Cancel(); }
            catch (Exception error) { failures.Add(error); }
            try { if (inputLifetime is not null) await inputLifetime.ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            try { if (input is not null) await input.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            try { await host.CloseAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
        await Console.Error.WriteLineAsync("MCP_HTTP_STOPPED");
    }
    private static async Task StopOnInputAsync(Stream input, CancellationTokenSource shutdown)
    {
        Exception? readFailure = null;
        try
        {
            var buffer = new byte[1024];
            while (await input.ReadAsync(buffer, shutdown.Token).ConfigureAwait(false) != 0) { }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) { readFailure = error; }
        try { shutdown.Cancel(); }
        catch (Exception cleanupError) when (readFailure is not null)
        {
            throw new AggregateException(readFailure, cleanupError);
        }
        if (readFailure is not null) ExceptionDispatchInfo.Capture(readFailure).Throw();
    }
}
