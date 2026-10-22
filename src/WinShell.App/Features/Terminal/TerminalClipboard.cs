using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace WinShell.App.Features.Terminal;

internal static partial class TerminalClipboard
{
    internal static string ReadBoundedText()
    {
        if (!OpenClipboard(0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开剪贴板。");
        try
        {
            var handle = GetClipboardData(13);
            if (handle == 0) return "";
            var size = GlobalSize(handle);
            if (size is < 2 or > 131074) throw new InvalidOperationException("剪贴板文本超过终端粘贴限制。");
            var pointer = GlobalLock(handle);
            if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var length = 0;
                while (length < (int)(size / 2) && Marshal.ReadInt16(pointer, length * 2) != 0) length++;
                if (length == (int)(size / 2)) throw new InvalidDataException("剪贴板文本缺少结束标记。");
                var text = Marshal.PtrToStringUni(pointer, length);
                return new UTF8Encoding(false, true).GetByteCount(text) <= 65536 ? text
                    : throw new InvalidOperationException("粘贴内容不能超过64KiB UTF8。");
            }
            finally { _ = GlobalUnlock(handle); }
        }
        finally { _ = CloseClipboard(); }
    }
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint GetClipboardData(uint format);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nuint GlobalSize(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);
}
