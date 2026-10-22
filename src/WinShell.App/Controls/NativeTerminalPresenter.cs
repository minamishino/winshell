using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.UI.Xaml.Controls;

namespace WinShell.App.Controls;

// Owned by the XAML dispatcher. The renderer must actually exit before this HWND is released.
internal sealed partial class NativeTerminalPresenter : Grid, IDisposable
{
    private HostWindow? _host;
    public nint Window => _host?.DangerousGetHandle() ?? 0;
    public void SetVisible(bool visible, bool allowInput = true)
    {
        if (_host is null) return;
        // Stop native keyboard admission synchronously, including while a popup keeps the surface visible.
        // Clear only focus inside this owned subtree; the visible WinUI overlay chooses its target.
        var focused = GetFocus();
        if ((!visible || !allowInput) && (focused == Window || IsChild(Window, focused))) _ = SetFocus(0);
        _ = EnableWindow(Window, visible && allowInput);
        _ = ShowWindow(Window, visible ? 4 : 0);
    }
    public void CreateHost()
    {
        if (_host is not null) throw new InvalidOperationException("Host already exists.");
        var windowId = XamlRoot?.ContentIslandEnvironment?.AppWindowId ?? throw new InvalidOperationException("Live WinUI XamlRoot required.");
        var parent = Microsoft.UI.Win32Interop.GetWindowFromWindowId(windowId);
        _host = CreateWindowEx(0, "STATIC", null, 0x56000000, 0, 0, 640, 400, parent, 0, 0, 0);
        if (_host.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        UpdateBounds();
    }
    public void UpdateBounds()
    {
        if (_host is null || XamlRoot is null) return;
        var origin = TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = XamlRoot.RasterizationScale;
        // Keep this owned child above the covering WinUI site without activating the window.
        if (!SetWindowPos(Window, 0, (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale), (int)Math.Max(16, ActualWidth * scale), (int)Math.Max(32, ActualHeight * scale), 0x10)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Host release belongs to XAML dispatcher.");
        _host?.Dispose();
        if (_host?.Failure is { } error) throw error;
        _host = null;
    }
    private sealed partial class HostWindow : SafeHandleZeroOrMinusOneIsInvalid
    {
        public HostWindow() : base(true) { }
        public Exception? Failure { get; private set; }
        protected override bool ReleaseHandle() { if (DestroyWindow(handle)) return true; Failure = new Win32Exception(Marshal.GetLastWin32Error()); return false; }
    }
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial HostWindow CreateWindowEx(uint extended, string name, string? title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();
    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(nint parent, nint window);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
