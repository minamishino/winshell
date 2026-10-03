using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinShell.Mcp.Transport;

/// <summary>Duplicate owned synchronous pipe handles; .NET FileStream supplies cancellable async-over-sync IO.</summary>
public static class StandardPipeStreams
{
    public static FileStream OpenInput() => Open(-10, FileAccess.Read);
    public static FileStream OpenOutput() => Open(-11, FileAccess.Write);
    public static FileStream OpenError() => Open(-12, FileAccess.Write);

    private static FileStream Open(int identifier, FileAccess access)
    {
        var original = GetStdHandle(identifier);
        if (GetFileType(original) != 3) throw new InvalidOperationException("Redirected pipes required.");
        var process = GetCurrentProcess();
        if (!DuplicateHandle(process, original, process, out var duplicate, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try { return new FileStream(duplicate, access, bufferSize: 1, isAsync: false); }
        catch { duplicate.Dispose(); throw; }
    }

    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int identifier);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess,
        out SafeFileHandle targetHandle, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
