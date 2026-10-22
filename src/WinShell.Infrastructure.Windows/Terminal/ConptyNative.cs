using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinShell.Infrastructure.Windows.Terminal;

internal static partial class ConptyNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }
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
    [LibraryImport("conpty.dll")] internal static partial int ConptyCreatePseudoConsole(Coord size, SafePipeHandle input, SafePipeHandle output, uint flags, out PseudoConsoleHandle console);
    [LibraryImport("conpty.dll")] internal static partial int ConptyResizePseudoConsole(PseudoConsoleHandle console, Coord size);
    [LibraryImport("conpty.dll")] internal static partial int ConptyReleasePseudoConsole(PseudoConsoleHandle console);
    [LibraryImport("conpty.dll")] private static partial void ConptyClosePseudoConsole(nint console);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previousValue, nint returnSize);
    [LibraryImport("kernel32.dll")] private static partial void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfoEx startupInfo, out ProcessInformation information);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
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
    public static int RequestTermination(SafeProcessHandle process, uint exitCode)
    {
        // Capture before any other native call. Classification follows the owner's actual joins.
        return TerminateProcess(process, exitCode) ? 0 : Marshal.GetLastWin32Error();
    }
    public static void VerifyTermination(SafeProcessHandle process, int terminationError)
    {
        // A concurrent exit can return access denied before the process object becomes signaled.
        // Only a fresh OS query after joining may accept that error; every other error survives.
        if (terminationError != 0 && (terminationError != 5 || WaitForSingleObject(process, 0) != 0))
            throw new Win32Exception(terminationError);
    }
    public static int WaitForExit(SafeProcessHandle process)
    {
        if (WaitForSingleObject(process, uint.MaxValue) != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned process wait failed.");
        if (!GetExitCodeProcess(process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((int)code);
    }
}
