using System.Runtime.InteropServices;

namespace WinShell.Terminal.NativeHost;
internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not ["--renderer"]) return 2;
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
        try { return new NativeRendererWindow().Run(); }
        finally { CoUninitialize(); }
    }
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
}
