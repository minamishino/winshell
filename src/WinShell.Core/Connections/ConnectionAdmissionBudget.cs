using WinShell.Core.Ssh;
namespace WinShell.Core.Connections;

// One instance per runtime. Leases belong to sessions, including their closing workers.
public sealed class ConnectionAdmissionBudget
{
    private readonly SemaphoreSlim _connections = new(16, 16);
    private readonly SemaphoreSlim _handshakes = new(4, 4);
    private readonly SemaphoreSlim _transfers = new(4, 4);
    public IDisposable AdmitConnection() => Admit(_connections, "连接已达到 16 个上限（包括正在关闭的连接）。");
    public IDisposable AdmitTransfer() => Admit(_transfers, "传输已达到 4 个上限。");
    public async Task<IDisposable> EnterHandshakeAsync(CancellationToken cancellationToken)
    {
        await _handshakes.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_handshakes);
    }
    private static IDisposable Admit(SemaphoreSlim slots, string message)
    {
        if (!slots.Wait(0)) throw new SshConnectionException(SshFailureKind.Capacity, message);
        return new Lease(slots);
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
