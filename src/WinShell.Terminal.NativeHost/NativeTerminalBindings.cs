/*
Derived from 0x5bfa/WindowsTerminalControl, commit 8a9d4e05484ee848098471404e312fbd4ebaee16
src/WindowsTerminalControl.WinUI/TerminalControl.cs
WinShell adaptation: ChildWindow embedding owned by one isolated helper process;
guarded native callbacks and acknowledged output, with native graph released at process exit.
Experimental fixed embedding ABI snapshot from a stable Microsoft Terminal binary release;
the flat exports do not carry an official long-term compatibility guarantee.

MIT License

Copyright (c) 0x5bfa

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

The complete Microsoft Terminal license and native third-party notices are deployed
as ThirdPartyNotices.txt and NOTICE.html alongside the fixed native DLL.

*/
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace WinShell.Terminal.NativeHost;
internal static class NativeTerminalBindings
    {
        private const string TerminalControlLibrary = "Microsoft.Terminal.Control.dll";

        internal const int ShowWindowNoActivate = 4;
        internal const int SystemMetricDoubleClickWidth = 36;
        internal const int SystemMetricDoubleClickHeight = 37;
        internal const uint WindowStyleTerminalHost = 0x56000000;
        internal const uint SetWindowPosNoZOrder = 0x0004;
        internal const uint SetWindowPosNoActivate = 0x0010;
        internal const uint MouseKeyLeftButton = 0x0001;
        internal const uint MouseKeyRightButton = 0x0002;
        internal const uint MouseKeyShift = 0x0004;
        internal const uint MouseKeyControl = 0x0008;
        internal const uint MouseKeyMiddleButton = 0x0010;
        internal const uint WmSetFocus = 0x0007;
        internal const uint WmKillFocus = 0x0008;
        internal const uint WmKeyDown = 0x0100;
        internal const uint WmKeyUp = 0x0101;
        internal const uint WmChar = 0x0102;
        internal const uint WmSystemKeyDown = 0x0104;
        internal const uint WmSystemKeyUp = 0x0105;
        internal const uint WmSystemChar = 0x0106;
        internal const uint WmMouseActivate = 0x0021;
        internal const uint WmMouseMove = 0x0200;
        internal const uint WmLeftButtonDown = 0x0201;
        internal const uint WmLeftButtonUp = 0x0202;
        internal const uint WmLeftButtonDoubleClick = 0x0203;
        internal const uint WmRightButtonDown = 0x0204;
        internal const uint WmRightButtonUp = 0x0205;
        internal const uint WmRightButtonDoubleClick = 0x0206;
        internal const uint WmMiddleButtonDown = 0x0207;
        internal const uint WmMiddleButtonUp = 0x0208;
        internal const uint WmMiddleButtonDoubleClick = 0x0209;
        internal const uint WmMouseWheel = 0x020A;
        internal const uint WmMouseHorizontalWheel = 0x020E;
        internal const uint WmMouseLeave = 0x02A3;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void WriteCallback(nint data);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void ScrollCallback(int viewTop, int viewHeight, int bufferSize);

        internal enum CaretStyle
        {
            BlinkingBlock = 1,
            SteadyBlock = 2,
            BlinkingUnderline = 3,
            SteadyUnderline = 4,
            BlinkingBar = 5,
            SteadyBar = 6
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TerminalTheme
        {
            internal uint DefaultBackground;
            internal uint DefaultForeground;
            internal uint DefaultSelectionBackground;
            internal CaretStyle CursorStyle;

            [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
            internal uint[] ColorTable;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TerminalSizeNative
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            internal NativePoint(int x, int y)
            {
                X = x;
                Y = y;
            }

            internal int X;
            internal int Y;
        }

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern int CreateTerminal(nint parent, out nint window, out TerminalHandle terminal);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void DestroyTerminal(nint terminal);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalRegisterWriteCallback(TerminalHandle terminal, [MarshalAs(UnmanagedType.FunctionPtr)] WriteCallback callback);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalRegisterScrollCallback(TerminalHandle terminal, [MarshalAs(UnmanagedType.FunctionPtr)] ScrollCallback callback);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSendOutput(TerminalHandle terminal, string data);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSendKeyEvent(TerminalHandle terminal, ushort virtualKey, ushort scanCode, ushort flags, [MarshalAs(UnmanagedType.I1)] bool keyDown);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSendCharEvent(TerminalHandle terminal, char character, ushort scanCode, ushort flags);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern int TerminalTriggerResize(TerminalHandle terminal, int width, int height, out TerminalSizeNative dimensions);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalDpiChanged(TerminalHandle terminal, int dpi);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalUserScroll(TerminalHandle terminal, int viewTop);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]

        internal static extern nint TerminalGetSelection(TerminalHandle terminal);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool TerminalIsSelectionActive(TerminalHandle terminal);

        [DllImport(TerminalControlLibrary, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSetTheme(TerminalHandle terminal, [MarshalAs(UnmanagedType.Struct)] TerminalTheme theme, string fontFamily, int fontSize, int dpi);

        [DllImport(TerminalControlLibrary, CallingConvention = CallingConvention.StdCall)]
        internal static extern void TerminalSetFocused(TerminalHandle terminal, [MarshalAs(UnmanagedType.I1)] bool focused);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowEx(uint extendedStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetFocus(nint window);

        [DllImport("user32.dll")]
        internal static extern nint GetFocus();

        [DllImport("user32.dll")]
        internal static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(nint window, ref NativePoint point);

        [DllImport("user32.dll")]
        internal static extern short GetKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        internal static bool IsKeyDown(int virtualKey)
        {
            return (GetKeyState(virtualKey) & 0x8000) != 0;
        }
    }
internal sealed class TerminalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public TerminalHandle() : base(true) { }
    // Native object is owned for the whole helper lifetime. OS process exit releases it.
    // Calling the known-unsafe same-process destructor is deliberately not the shutdown path.
    protected override bool ReleaseHandle() => true;
}
