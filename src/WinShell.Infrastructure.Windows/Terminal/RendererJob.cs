using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinShell.Infrastructure.Windows.Terminal;

public sealed class RendererJob : IDisposable
{
    private readonly JobHandle _handle = CreateJobObject(0, null);
    private readonly object _gate = new();
    private int _active;
    private bool _disposed;
    public RendererJob()
    {
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new Limits { Basic = new BasicLimits { LimitFlags = 0x2308, ActiveProcessLimit = 2 }, ProcessMemoryLimit = 128u * 1024 * 1024, JobMemoryLimit = 256u * 1024 * 1024 };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<Limits>()))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            _handle.Dispose();
            throw error;
        }
    }
    internal void Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active == 2) throw new InvalidOperationException("最多同时打开两个终端，请等待一个终端关闭。");
            _active++;
        }
    }
    internal void Release() { lock (_gate) _active--; }
    internal void Assign(SafeProcessHandle process)
    {
        if (!AssignProcessToJobObject(_handle, process)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Assign suspended renderer to constrained Job failed.");
    }
    public int ActiveCount { get { lock (_gate) return _active; } }
    public Limits Query() => Query(_handle.DangerousGetHandle());
    public static Limits QueryCurrentProcessJob() => Query(0);
    private static Limits Query(nint handle)
    {
        if (!QueryInformationJobObject(handle, 9, out var limits, (uint)Marshal.SizeOf<Limits>(), 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return limits;
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _handle.Dispose(); } }
    [StructLayout(LayoutKind.Sequential)] public struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Limits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", EntryPoint="CreateJobObjectW", CharSet=CharSet.Unicode, SetLastError=true)] private static extern JobHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool QueryInformationJobObject(nint job, int infoClass, out Limits limits, uint length, nint returned);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref Limits limits, uint length);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CloseHandle(nint handle);
}
