using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.UI.Xaml.Controls;

namespace WinShell.App.Controls;

// Owned by the XAML dispatcher. The renderer must actually exit before this HWND is released.
internal sealed class NativeTerminalPresenter : Grid, IDisposable
{
    private HostWindow? _host;
    public nint Window => _host?.DangerousGetHandle() ?? 0;
    public void SetVisible(bool visible, bool allowInput = true)
    {
        if (_host is null) return;
        // Stop native keyboard admission synchronously, including while a popup keeps the surface visible.
        // Clear only focus inside this owned subtree; the visible WinUI overlay chooses its target.
        var focused = GetFocus();
        if ((!visible || !allowInput) && (focused == Window || IsChild(Window, focused))) SetFocus(0);
        EnableWindow(Window, visible && allowInput);
        ShowWindow(Window, visible ? 4 : 0);
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
        var origin = TransformToVisual(null).TransformPoint(new(0, 0));
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
    private sealed class HostWindow : SafeHandleZeroOrMinusOneIsInvalid
    {
        public HostWindow() : base(true) { }
        public Exception? Failure { get; private set; }
        protected override bool ReleaseHandle() { if (DestroyWindow(handle)) return true; Failure = new Win32Exception(Marshal.GetLastWin32Error()); return false; }
    }
    [DllImport("user32.dll", EntryPoint="CreateWindowExW", CharSet=CharSet.Unicode, SetLastError=true)] private static extern HostWindow CreateWindowEx(uint extended, string name, string? title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint window);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
