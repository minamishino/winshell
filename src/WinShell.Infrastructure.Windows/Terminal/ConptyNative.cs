using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinShell.Infrastructure.Windows.Terminal;

internal static class ConptyNative
{
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public nint ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    internal sealed class PseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PseudoConsoleHandle() : base(true) { }
        protected override bool ReleaseHandle() { ConptyClosePseudoConsole(handle); return true; }
    }
    private sealed class ThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ThreadHandle(nint value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("conpty.dll", ExactSpelling = true)] internal static extern int ConptyCreatePseudoConsole(Coord size, SafePipeHandle input, SafePipeHandle output, uint flags, out PseudoConsoleHandle console);
    [DllImport("conpty.dll", ExactSpelling = true)] internal static extern int ConptyResizePseudoConsole(PseudoConsoleHandle console, Coord size);
    [DllImport("conpty.dll", ExactSpelling = true)] internal static extern int ConptyReleasePseudoConsole(PseudoConsoleHandle console);
    [DllImport("conpty.dll", ExactSpelling = true)] private static extern void ConptyClosePseudoConsole(nint console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previousValue, nint returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfoEx startupInfo, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    public static (SafeProcessHandle Handle, int Id) Start(string executable, StringBuilder commandLine, string? directory, PseudoConsoleHandle console)
    {
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size == 0 || Marshal.GetLastWin32Error() != 122) throw new Win32Exception(Marshal.GetLastWin32Error(), "Attribute list sizing failed.");
        var attributes = Marshal.AllocHGlobal(checked((nint)size));
        var initialized = false;
        var consoleReference = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            console.DangerousAddRef(ref consoleReference);
            if (!UpdateProcThreadAttribute(attributes, 0, 0x00020016, console.DangerousGetHandle(), (nuint)nint.Size, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Explicit null std handles prevent inheriting a redirected parent's stdout/stderr.
            // Microsoft Terminal discussion #15814, maintainer guidance; console creates its handles.
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 }, Attributes = attributes };
            if (!CreateProcess(executable, commandLine, 0, 0, false, 0x00080000, 0, directory, ref startup, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned ConPTY process creation failed.");
            using var thread = new ThreadHandle(information.Thread);
            return (new SafeProcessHandle(information.Process, true), checked((int)information.ProcessId));
        }
        finally
        {
            if (consoleReference) console.DangerousRelease();
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }
    public static int WaitForExit(SafeProcessHandle process)
    {
        if (WaitForSingleObject(process, uint.MaxValue) != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned process wait failed.");
        if (!GetExitCodeProcess(process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((int)code);
    }
}
