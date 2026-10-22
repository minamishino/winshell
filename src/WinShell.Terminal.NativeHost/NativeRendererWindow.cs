using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WinShell.Terminal.Shared;
using static WinShell.Terminal.NativeHost.NativeTerminalBindings;

namespace WinShell.Terminal.NativeHost;

internal sealed partial class NativeRendererWindow
{
    private readonly RendererFrameCodec _reader = new(Console.OpenStandardInput());
    private readonly RendererFrameCodec _writer = new(Console.OpenStandardOutput());
    private readonly SemaphoreSlim _writeSerial = new(1);
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly char[] _characters = new char[65537];
    private readonly WriteCallback _writeCallback;
    private readonly ScrollCallback _scrollCallback;
    private readonly SubclassCallback _subclassCallback;
    private TerminalHandle? _terminal;
    private nint _window;
    private uint _thread;
    private RendererFrame? _pending;
    private TaskCompletionSource? _consumed;
    private bool _closing;
    private bool _visible, _inputReady;
    private Exception? _failure;
    private int _inputCount;
    private char? _pendingHighSurrogate;
    private (ushort VirtualKey, ushort ScanCode)? _lastKeyEvent;
    private byte[]? _sizeReply;
    private byte[]? _selectionReply;
    private byte[]? _pendingScroll;
    private int _scrollSending;
    private int _maximumWidth, _maximumHeight;
    private readonly Lock _inputGate = new();
    private readonly List<Task> _writes = [];

    public NativeRendererWindow()
    {
        _writeCallback = WriteInput;
        _scrollCallback = OnScroll;
        _subclassCallback = Subclass;
    }
    public int Run()
    {
        _thread = GetCurrentThreadId();
        _ = PeekMessage(out _, 0, 0, 0, 0); // Create the STA message queue before the reader posts; no message is required yet.
        _ = Task.Run(ReadLoopAsync);
        while (!_closing)
        {
            var result = GetMessage(out var message, 0, 0, 0);
            if (result <= 0) { if (result < 0) _failure = new Win32Exception(Marshal.GetLastWin32Error()); break; }
            if (message.Id == 0x8001)
            {
                try { if (_pending is { } frame) Consume(frame); }
                catch (Exception error) { _failure = error; _closing = true; }
                finally { _pending = null; _consumed?.TrySetResult(); }
            }
            else { _ = TranslateMessage(ref message); _ = DispatchMessage(ref message); }
        }
        // This is the message-loop frame, never a SendMessage/subclass callback stack.
        _closing = true;
        var disconnected = UiaDisconnectAllProviders();
        if (disconnected < 0) _failure ??= Marshal.GetExceptionForHR(disconnected);
        GC.KeepAlive(_terminal); GC.KeepAlive(_writeCallback); GC.KeepAlive(_scrollCallback); GC.KeepAlive(_subclassCallback);
        // The process owns the opaque native graph. Avoid the previously crashing DestroyTerminal/WM_DESTROY path.
        Environment.Exit(_failure is null ? 0 : 3);
        return 3;
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                if (frame.Type == RendererFrameType.InputAck) { lock (_inputGate) { if (_inputCount == 0) throw new InvalidDataException("Unexpected input ACK."); _inputCount--; } continue; }
                _consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = frame;
                if (!PostThreadMessage(_thread, 0x8001, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                await _consumed.Task.ConfigureAwait(false);
                if (_closing) return;
                var replyType = frame.Type switch
                {
                    RendererFrameType.Initialize => RendererFrameType.Ready,
                    RendererFrameType.Resize => RendererFrameType.Size,
                    RendererFrameType.Copy => RendererFrameType.Selection,
                    _ => RendererFrameType.Ack
                };
                var replyPayload = frame.Type switch
                {
                    RendererFrameType.Initialize => BitConverter.GetBytes(_window.ToInt64()),
                    RendererFrameType.Resize => _sizeReply!,
                    RendererFrameType.Copy => _selectionReply!,
                    _ => BitConverter.GetBytes(frame.Sequence)
                };
                await SendAsync(replyType, replyPayload).ConfigureAwait(false);
            }
        }
        catch (Exception error) { _failure = error; _pending = new RendererFrame(RendererFrameType.Close, 0, default); PostThreadMessage(_thread, 0x8001, 0, 0); }
    }
    private unsafe void Consume(RendererFrame frame)
    {
        switch (frame)
        {
            case { Type: RendererFrameType.Close }: _closing = true; return;
            case { Type: RendererFrameType.Initialize }:
                if (_terminal is not null || frame.Payload.Length != 8) throw new InvalidDataException("Invalid initialization.");
                var parent = (nint)BinaryPrimitives.ReadInt64LittleEndian(frame.Payload.Span);
                if (!IsWindow(parent) || !AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(parent), GetThreadDpiAwarenessContext())) throw new InvalidOperationException("Live parent and matching DPI awareness required.");
                if (GetWindowThreadProcessId(parent, out var parentProcess) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the terminal host process.");
                if (parentProcess == Environment.ProcessId) throw new InvalidOperationException("Renderer requires cross-process parent.");
                Marshal.ThrowExceptionForHR(CreateTerminal(parent, out _window, out _terminal));
                if (!IsWindow(_window) || GetParent(_window) != parent) throw new InvalidOperationException("Native child not attached to the WinUI host.");
                if (!SetWindowSubclass(_window, _subclassCallback, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                _ = EnableWindow(_window, false); // Previous enabled state is irrelevant; startup must finish before input is admitted.
                TerminalRegisterWriteCallback(_terminal, _writeCallback);
                TerminalRegisterScrollCallback(_terminal, _scrollCallback);
                TerminalDpiChanged(_terminal, checked((int)GetDpiForWindow(parent)));
                Marshal.ThrowExceptionForHR(TerminalTriggerResize(_terminal, 640, 400, out var dimensions));
                if (dimensions.X is < 1 or > 400 || dimensions.Y is < 1 or > 160) throw new InvalidOperationException("Native viewport exceeds budget.");
                _maximumWidth = Math.Max(1, 640 / (dimensions.X + 1)) * 400;
                _maximumHeight = Math.Max(1, 400 / (dimensions.Y + 1)) * 160;
                if (!SetWindowPos(_window, 0, 0, 0, 640, 400, 0x14)) throw new Win32Exception(Marshal.GetLastWin32Error());
                ShowWindow(_window, 4);
                return;
        }
        if (_terminal is null) throw new InvalidDataException("Initialization must be first.");
        switch (frame.Type)
        {
            case RendererFrameType.Scroll:
                if (frame.Payload.Length != 4) throw new InvalidDataException();
                TerminalUserScroll(_terminal, Math.Clamp(BitConverter.ToInt32(frame.Payload.Span), 0, 9001));
                break;
            case RendererFrameType.Copy:
                if (!frame.Payload.IsEmpty) throw new InvalidDataException();
                _selectionReply = CopySelection();
                break;
            case RendererFrameType.Theme:
                var (_, dpi, background, foreground, fontFamily, fontSize, cursorStyle) = RendererThemePayload.Decode(frame.Payload.Span);
                var theme = new TerminalTheme
                {
                    DefaultBackground = background,
                    DefaultForeground = foreground,
                    DefaultSelectionBackground = 0x808080,
                    CursorStyle = (CaretStyle)cursorStyle
                };
                ReadOnlySpan<uint> colors = [0x0C0C0C,0x1F0FC5,0x0EA113,0x009CC1,0xDA3700,0x981788,0xDD963A,0xCCCCCC,0x767676,0x5648E7,0x0CC616,0xA5F1F9,0xFF783B,0x9E00B4,0xD6D661,0xF2F2F2];
                colors.CopyTo(new Span<uint>(theme.ColorTable, 16));
                TerminalSetTheme(_terminal, theme, fontFamily == 0 ? "Cascadia Code" : "Consolas", fontSize, dpi);
                TerminalDpiChanged(_terminal, dpi);
                Marshal.ThrowExceptionForHR(TerminalTriggerResize(_terminal, 640, 400, out var baseline));
                _maximumWidth = Math.Max(1, 640 / (baseline.X + 1)) * 400;
                _maximumHeight = Math.Max(1, 400 / (baseline.Y + 1)) * 160;
                break;
            case RendererFrameType.Output:
            case RendererFrameType.CompleteOutput:
                var count = _decoder.GetChars(frame.Payload.Span, _characters, frame.Type == RendererFrameType.CompleteOutput);
                var start = 0;
                for (var index = 0; index <= count; index++)
                    if (index == count || _characters[index] == '\0')
                    {
                        if (index > start) TerminalSendOutput(_terminal, new string(_characters, start, index - start));
                        start = index + 1;
                    }
                break;
            case RendererFrameType.Focus:
                if (frame.Payload.Length != 1) throw new InvalidDataException();
                _inputReady = true;
                _ = EnableWindow(_window, _visible);
                var focused = frame.Payload.Span[0] != 0 && _visible;
                if (focused) SetFocus(_window);
                else if (GetFocus() == _window) SetFocus(0);
                TerminalSetFocused(_terminal, focused);
                break;
            case RendererFrameType.Resize:
                if (frame.Payload.Length != 8) throw new InvalidDataException();
                var width = Math.Clamp(BitConverter.ToInt32(frame.Payload.Span), 16, _maximumWidth);
                var height = Math.Clamp(BitConverter.ToInt32(frame.Payload.Span[4..]), 32, _maximumHeight);
                Marshal.ThrowExceptionForHR(TerminalTriggerResize(_terminal, width, height, out var size));
                if (size.X is < 1 or > 400 || size.Y is < 1 or > 160) throw new InvalidOperationException("Native viewport exceeds budget.");
                if (!SetWindowPos(_window, 0, 0, 0, width, height, 0x14)) throw new Win32Exception(Marshal.GetLastWin32Error());
                _sizeReply = new byte[8]; BitConverter.TryWriteBytes(_sizeReply.AsSpan(), size.X); BitConverter.TryWriteBytes(_sizeReply.AsSpan(4), size.Y);
                break;
            case RendererFrameType.Visibility:
                if (frame.Payload.Length != 1) throw new InvalidDataException();
                _visible = frame.Payload.Span[0] != 0;
                if (!_visible)
                {
                    if (GetFocus() == _window) SetFocus(0);
                    TerminalSetFocused(_terminal, false);
                }
                _ = EnableWindow(_window, _visible && _inputReady);
                ShowWindow(_window, _visible ? 4 : 0);
                break;
            case RendererFrameType.Initialize:
            case RendererFrameType.Close:
            case RendererFrameType.Ready:
            case RendererFrameType.Ack:
            case RendererFrameType.Input:
            case RendererFrameType.InputAck:
            case RendererFrameType.Size:
            case RendererFrameType.ScrollState:
            case RendererFrameType.Selection:
            default: throw new InvalidDataException("Unsupported frame for renderer state.");
        }
    }
    private unsafe byte[] CopySelection()
    {
        if (!TerminalIsSelectionActive(_terminal!)) return [];
        var pointer = TerminalGetSelection(_terminal!);
        if (pointer == 0) return [];
        try
        {
            var text = (char*)pointer;
            var length = 0;
            while (length < 32768 && text[length] != '\0') length++;
            if (length == 32768) throw new InvalidDataException("Selection exceeds bounded UTF16 scan.");
            var value = new ReadOnlySpan<char>(text, length);
            var encoding = new UTF8Encoding(false, true);
            return encoding.GetByteCount(value) <= 65536
                ? encoding.GetBytes(value.ToString())
                : throw new InvalidDataException("Selection exceeds 64KiB UTF8.");
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    private void OnScroll(int top, int height, int total)
    {
        try
        {
            if (_closing) return;
            var bytes = new byte[12];
            BitConverter.TryWriteBytes(bytes.AsSpan(), top); BitConverter.TryWriteBytes(bytes.AsSpan(4), height); BitConverter.TryWriteBytes(bytes.AsSpan(8), total);
            Interlocked.Exchange(ref _pendingScroll, bytes);
            if (Interlocked.Exchange(ref _scrollSending, 1) == 0) _ = SendScrollAsync();
        }
        catch (Exception error) { _failure = error; _closing = true; PostThreadMessage(_thread, 0x8001, 0, 0); }
    }
    private async Task SendScrollAsync()
    {
        try
        {
            while (!_closing)
            {
                var state = Interlocked.Exchange(ref _pendingScroll, null);
                if (state is null) break;
                await SendAsync(RendererFrameType.ScrollState, state).ConfigureAwait(false);
            }
        }
        catch (Exception error) { _failure = error; _closing = true; PostThreadMessage(_thread, 0x8001, 0, 0); }
        finally
        {
            Interlocked.Exchange(ref _scrollSending, 0);
            if (!_closing && Volatile.Read(ref _pendingScroll) is not null && Interlocked.Exchange(ref _scrollSending, 1) == 0) _ = SendScrollAsync();
        }
    }
    private unsafe void WriteInput(nint text)
    {
        try
        {
            if (_closing) return;
            var characters = (char*)text;
            var length = 0;
            while (length < 32768 && characters[length] != '\0') length++;
            if (length == 32768) throw new InvalidDataException("Native input exceeds bounded UTF16 scan.");
            var encoding = new UTF8Encoding(false, true);
            var value = new string(characters, 0, length);
            if (_pendingHighSurrogate is { } high) { value = high + value; _pendingHighSurrogate = null; }
            if (value.Length > 0 && char.IsHighSurrogate(value[^1])) { _pendingHighSurrogate = value[^1]; value = value[..^1]; }
            if (value.Length == 0) return;
            if (encoding.GetByteCount(value) > RendererFrameCodec.MaximumPayload) throw new InvalidDataException("Native input exceeds 64KiB.");
            lock (_inputGate)
            {
                if (_inputCount == 4) throw new InvalidOperationException("Native input admission full.");
                _inputCount++;
                _writes.RemoveAll(task => task.IsCompletedSuccessfully);
                _writes.Add(SendInputAsync(encoding.GetBytes(value)));
            }
        }
        catch (Exception error) { _failure = error; _closing = true; PostThreadMessage(_thread, 0x8001, 0, 0); }
    }
    private async Task SendInputAsync(byte[] bytes)
    {
        try { await SendAsync(RendererFrameType.Input, bytes).ConfigureAwait(false); }
        catch (Exception error) { _failure = error; _closing = true; PostThreadMessage(_thread, 0x8001, 0, 0); }
    }
    private async Task SendAsync(RendererFrameType type, ReadOnlyMemory<byte> payload)
    {
        await _writeSerial.WaitAsync().ConfigureAwait(false);
        try { await _writer.WriteAsync(type, payload, CancellationToken.None).ConfigureAwait(false); }
        finally { _writeSerial.Release(); }
    }
    private void SendKey(ushort virtualKey, ushort scanCode, ushort flags, bool keyDown)
    {
        // Mirror the native Store/Take pair, including key-up metadata, on this message thread.
        _lastKeyEvent = (virtualKey, scanCode);
        TerminalSendKeyEvent(_terminal!, virtualKey, scanCode, flags, keyDown);
    }
    private void SendCharacter(char character, ushort scanCode, ushort flags)
    {
        var previousKey = _lastKeyEvent;
        _lastKeyEvent = null;
        var matchingKey = previousKey is { VirtualKey: > 0 and < 0xff } key && key.ScanCode == scanCode;
        if (!matchingKey && (scanCode == 0 || MapVirtualKeyW(scanCode, 1) == 0))
        {
            var characterKey = VkKeyScanW(character);
            if (characterKey is -1 or > byte.MaxValue)
            {
                // Reject the fixed engine's unmapped/packed-shift fallback, preserving safe Enter/Backspace keys.
                // VK_PACKET seeds text without an extra input event when its scan is zero.
                TerminalSendKeyEvent(_terminal!, 0xe7, 0, flags, true);
                scanCode = 0;
            }
        }
        TerminalSendCharEvent(_terminal!, character, scanCode, flags);
    }
    private nint Subclass(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (_closing && message == 0x3D) return 0;
        try
        {
            if (_closing || _terminal is null) return DefSubclassProc(window, message, wParam, lParam);
            var scan = (ushort)(((ulong)lParam >> 16) & 0xff);
            var flags = (ushort)(((ulong)lParam >> 16) & 0xff00);
            switch (message)
            {
                case 0x21: SetFocus(_window); TerminalSetFocused(_terminal, true); return 1;
                case 0x100: case 0x104: SendKey((ushort)wParam, scan, flags, true); return 0;
                case 0x101: case 0x105: SendKey((ushort)wParam, scan, flags, false); return 0;
                case 0x102: case 0x106: SendCharacter((char)wParam, scan, flags); return 0;
                case 7: TerminalSetFocused(_terminal, true); break;
                case 8: TerminalSetFocused(_terminal, false); break;
                case 0x204: case 0x205: case 0x206: return 0; // Never enter native unbounded clipboard path.
            }
            return DefSubclassProc(window, message, wParam, lParam);
        }
        catch (Exception error) { _failure = error; _closing = true; PostThreadMessage(_thread, 0x8001, 0, 0); return 0; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    private delegate nint SubclassCallback(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll", SetLastError=true, EntryPoint = "PostThreadMessageA")]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", SetLastError=true, EntryPoint = "GetMessageA")] private static partial int GetMessage(out Message message, nint window, uint min, uint max);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageA")]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TranslateMessage(ref Message message);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageA")] private static partial nint DispatchMessage(ref Message message);
    [LibraryImport("user32.dll")] private static partial uint MapVirtualKeyW(uint code, uint mapping);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial short VkKeyScanW(char character);
    [LibraryImport("user32.dll")] private static partial nint GetParent(nint window);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint GetWindowThreadProcessId(nint window, out uint process);
    [LibraryImport("user32.dll")] private static partial nint GetWindowDpiAwarenessContext(nint window);
    [LibraryImport("user32.dll")] private static partial nint GetThreadDpiAwarenessContext();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
    [LibraryImport("user32.dll")] private static partial nint GetFocus();
    [LibraryImport("comctl32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowSubclass(nint window, SubclassCallback callback, nuint id, nuint data);
    [LibraryImport("comctl32.dll")] private static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("uiautomationcore.dll")] private static partial int UiaDisconnectAllProviders();
}
