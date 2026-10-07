using System.Runtime.InteropServices;

namespace WinShell.Terminal.NativeHost;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not ["--renderer"]) return 2;
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
        try { return new NativeRendererWindow().Run(); }
        finally { CoUninitialize(); }
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
