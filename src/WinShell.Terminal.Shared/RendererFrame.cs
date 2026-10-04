namespace WinShell.Terminal.Shared;

internal enum RendererFrameType : ushort { Initialize=1, Output, CompleteOutput, Resize, Scroll, Focus, Visibility, Close, Ready, Ack, Input, InputAck, Size, ScrollState, Copy, Selection, Theme,
}
internal readonly record struct RendererFrame(RendererFrameType Type, ulong Sequence, ReadOnlyMemory<byte> Payload);
