namespace WinShell.Core.Tunnels;
public sealed class TunnelAdmissionBudget
{
    private readonly SemaphoreSlim _slots = new(8, 8);
    public IDisposable Admit()
    {
        if (!_slots.Wait(0)) throw new InvalidOperationException("最多同时运行 8 个隧道（包括正在启动或关闭的隧道）。");
        return new Lease(_slots);
    }
    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
