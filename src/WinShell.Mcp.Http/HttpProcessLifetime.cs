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
        try
        {
            if (options.StdioLifetime)
            {
                input = StandardPipeStreams.OpenInput();
                inputLifetime = StopOnInputAsync(input, shutdown);
            }
            await host.WaitForShutdownAsync(shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            shutdown.Cancel();
            if (inputLifetime is not null) await inputLifetime.ConfigureAwait(false);
            if (input is not null) await input.DisposeAsync().ConfigureAwait(false);
            await host.CloseAsync().ConfigureAwait(false);
        }
        await Console.Error.WriteLineAsync("MCP_HTTP_STOPPED");
    }
    private static async Task StopOnInputAsync(Stream input, CancellationTokenSource shutdown)
    {
        try
        {
            var buffer = new byte[1024];
            while (await input.ReadAsync(buffer, shutdown.Token).ConfigureAwait(false) != 0) { }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally { shutdown.Cancel(); }
    }
}
