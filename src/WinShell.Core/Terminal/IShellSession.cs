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

// Optional completion result, implemented by the local process and remote SSH shell.
// Output EOF and process/channel exit are separate; inspect ExitCode after ProcessCompletion.
public interface IShellSessionExit
{
    Task ProcessCompletion { get; }
    int? ExitCode { get; }
}
