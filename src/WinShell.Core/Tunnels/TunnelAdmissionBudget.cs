namespace WinShell.Core.Tunnels;
public sealed class TunnelAdmissionBudget
{
    private readonly SemaphoreSlim _slots = new(8, 8);
    public IDisposable Admit()
    {
        return _slots.Wait(0) ? new Lease(_slots) : throw new InvalidOperationException("最多同时运行 8 个隧道（包括正在启动或关闭的隧道）。");
    }
    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
