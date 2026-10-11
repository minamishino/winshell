using System.Buffers.Binary;

namespace WinShell.Terminal.Shared;

// This fixed protocol uses IDs, never arbitrary strings crossing the native ABI boundary.
internal readonly record struct RendererThemePayload(bool Dark, int Dpi, uint BackgroundColor, uint ForegroundColor,
    byte FontFamily, int FontSize, byte CursorStyle)
{
    internal byte[] Encode()
    {
        Validate();
        var bytes = new byte[16]; bytes[0] = Dark ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), Dpi);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), BackgroundColor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9), ForegroundColor);
        bytes[13] = FontFamily; bytes[14] = checked((byte)FontSize); bytes[15] = CursorStyle;
        return bytes;
    }
    internal static RendererThemePayload Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is not (5 or 13 or 16) || bytes[0] > 1) throw new InvalidDataException("Invalid terminal theme payload.");
        var dark = bytes[0] != 0;
        var payload = new RendererThemePayload(dark, BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]),
            bytes.Length >= 13 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]) : dark ? 0x0C0C0Cu : 0xFFFFFFu,
            bytes.Length >= 13 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]) : dark ? 0xF2F2F2u : 0x0C0C0Cu,
            bytes.Length == 16 ? bytes[13] : (byte)0, bytes.Length == 16 ? bytes[14] : 12, bytes.Length == 16 ? bytes[15] : (byte)5);
        payload.Validate();
        return payload;
    }
    private void Validate()
    {
        if (Dpi is < 96 or > 960 || BackgroundColor > 0xFFFFFF || ForegroundColor > 0xFFFFFF || FontFamily > 1 || FontSize is < 8 or > 32 || CursorStyle is < 1 or > 6)
            throw new InvalidDataException("Unsupported terminal theme or preferences.");
    }
}
