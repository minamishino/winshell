using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinShell.Infrastructure.Windows.Terminal;

// The Job slot covers suspended startup through actual process/pipe completion.
public sealed class RendererProcessOwner : IAsyncDisposable
{
    private readonly RendererJob _job;
    private SafeProcessHandle? _process;
    private Task? _close;
    private readonly object _gate = new();
    private RendererProcessOwner(RendererJob job) { _job = job; }
    public AnonymousPipeServerStream Input { get; private set; } = null!;
    public AnonymousPipeServerStream Output { get; private set; } = null!;
    public Task<int> Completion { get; private set; } = Task.FromResult(-1);
    public int ProcessId { get; private set; }
    public bool WasTerminated { get; private set; }
    public ulong PrivateBytes
    {
        get
        {
            var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
            if (_process is null || !GetProcessMemoryInfo(_process, ref counters, counters.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (ulong)counters.PrivateUsage;
        }
    }
    public static async Task<RendererProcessOwner> StartAsync(RendererJob job, string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => await StartCoreAsync(job, executable, arguments, cancellationToken, null).ConfigureAwait(false);
    internal static Task<RendererProcessOwner> StartObservedAsync(RendererJob job, string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, Action<SafeProcessHandle> suspendedProcessObserved)
        => StartCoreAsync(job, executable, arguments, cancellationToken, suspendedProcessObserved);
    private static async Task<RendererProcessOwner> StartCoreAsync(RendererJob job, string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, Action<SafeProcessHandle>? suspendedProcessObserved)
    {
        var command = WindowsArguments.Build(executable, arguments);
        cancellationToken.ThrowIfCancellationRequested();
        job.Acquire();
        var owner = new RendererProcessOwner(job);
        try
        {
            owner.Input = new(PipeDirection.Out, HandleInheritability.Inheritable);
            owner.Output = new(PipeDirection.In, HandleInheritability.Inheritable);
            owner.CreateSuspended(executable, command, cancellationToken, suspendedProcessObserved);
            return owner;
        }
        catch (Exception initial)
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(initial, cleanup); }
            throw;
        }
    }
    private void CreateSuspended(string executable, StringBuilder command, CancellationToken cancellationToken, Action<SafeProcessHandle>? suspendedProcessObserved)
    {
        nuint bytes = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
        if (bytes == 0 || Marshal.GetLastWin32Error() != 122) throw new Win32Exception(Marshal.GetLastWin32Error());
        var attributes = Marshal.AllocHGlobal(checked((nint)bytes));
        var handles = Marshal.AllocHGlobal(nint.Size * 2);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            Marshal.WriteIntPtr(handles, Input.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, nint.Size, Output.ClientSafePipeHandle.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(2 * nint.Size), 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, StandardInput = Marshal.ReadIntPtr(handles), StandardOutput = Marshal.ReadIntPtr(handles, nint.Size) }, Attributes = attributes };
            if (!CreateProcess(executable, command, 0, 0, true, 0x80004 | 0x08000000, 0, Path.GetDirectoryName(executable), ref startup, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _process = new SafeProcessHandle(info.Process, true);
            using var thread = new ThreadHandle(info.Thread);
            ProcessId = checked((int)info.ProcessId);
            Completion = Task.Run(() =>
            {
                if (WaitForSingleObject(_process, uint.MaxValue) != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!GetExitCodeProcess(_process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return unchecked((int)code);
            });
            suspendedProcessObserved?.Invoke(_process);
            _job.Assign(_process);
            cancellationToken.ThrowIfCancellationRequested();
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            Input.DisposeLocalCopyOfClientHandle();
            Output.DisposeLocalCopyOfClientHandle();
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(attributes);
        }
    }
    public void Terminate()
    {
        lock (_gate)
        {
            if (_process is null || Completion.IsCompleted || WaitForSingleObject(_process, 0) == 0) return;
            if (!TerminateProcess(_process, 0xE0000001) && !Completion.IsCompleted) throw new Win32Exception(Marshal.GetLastWin32Error());
            WasTerminated = true;
        }
    }
    public ValueTask DisposeAsync() { lock (_gate) return new(_close ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        try
        {
            Terminate();
            await Completion.ConfigureAwait(false);
        }
        finally
        {
            Input?.Dispose(); Output?.Dispose(); _process?.Dispose(); _job.Release();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public nint ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool, QuotaPeakNonPagedPool, QuotaNonPagedPool, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
    [DllImport("psapi.dll", SetLastError=true)] private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
    private sealed class ThreadHandle : SafeHandleZeroOrMinusOneIsInvalid { public ThreadHandle(nint value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() => CloseHandle(handle); }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", EntryPoint="CreateProcessW", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(ThreadHandle thread);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CloseHandle(nint handle);
}
