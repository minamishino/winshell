namespace WinShell.Core.Terminal;

public interface IShellSession : IAsyncDisposable
{
    Stream Output { get; }
    Task OutputCompletion { get; }
    Task StartAsync(CancellationToken cancellationToken);
    ValueTask WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken);
    ValueTask ResizeAsync(ushort columns, ushort rows, CancellationToken cancellationToken);
    ValueTask CloseAsync(CancellationToken cancellationToken);
}
