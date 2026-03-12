namespace HidBridge.Hid;

/// <summary>
/// Builds HID input reports for mouse and keyboard actions.
/// </summary>
public static class HidReports
{
    /// <summary>
    /// Builds a mouse report using a parsed device-specific layout.
    /// </summary>
    /// <param name="layout">The parsed mouse layout.</param>
    /// <param name="buttons">The current mouse button bitmask.</param>
    /// <param name="dx">Relative X movement.</param>
    /// <param name="dy">Relative Y movement.</param>
    /// <param name="wheel">Relative wheel movement.</param>
    /// <returns>A serialized mouse input report.</returns>
    public static byte[] BuildMouseFromLayoutInfo(MouseLayoutInfo layout, byte buttons, int dx, int dy, int wheel)
    {
        var buttonsBits = layout.ButtonsCount * layout.ButtonsSizeBits;
        var maxBit = 0;
        if (buttonsBits > 0) maxBit = Math.Max(maxBit, layout.ButtonsOffsetBits + buttonsBits);
        if (layout.XSizeBits > 0) maxBit = Math.Max(maxBit, layout.XOffsetBits + layout.XSizeBits);
        if (layout.YSizeBits > 0) maxBit = Math.Max(maxBit, layout.YOffsetBits + layout.YSizeBits);
        if (layout.WheelSizeBits > 0) maxBit = Math.Max(maxBit, layout.WheelOffsetBits + layout.WheelSizeBits);

        var reportBytes = Math.Max(1, (maxBit + 7) / 8);
        var prefixBytes = layout.ReportId == 0 ? 0 : 1;
        var report = new byte[prefixBytes + reportBytes];
        var baseBit = prefixBytes * 8;

        if (layout.ReportId != 0)
        {
            report[0] = layout.ReportId;
        }

        if (buttonsBits > 0 && layout.ButtonsSizeBits > 0)
        {
            var count = Math.Min((int)layout.ButtonsCount, 8);
            for (var i = 0; i < count; i++)
            {
                var isDown = (buttons & (1 << i)) != 0;
                var bitOffset = baseBit + layout.ButtonsOffsetBits + (i * layout.ButtonsSizeBits);
                WriteBits(report, bitOffset, layout.ButtonsSizeBits, isDown ? 1 : 0);
            }
        }

        if (layout.XSizeBits > 0)
        {
            if (layout.XSigned) WriteSigned(report, baseBit + layout.XOffsetBits, layout.XSizeBits, dx);
            else WriteUnsigned(report, baseBit + layout.XOffsetBits, layout.XSizeBits, dx);
        }

        if (layout.YSizeBits > 0)
        {
            if (layout.YSigned) WriteSigned(report, baseBit + layout.YOffsetBits, layout.YSizeBits, dy);
            else WriteUnsigned(report, baseBit + layout.YOffsetBits, layout.YSizeBits, dy);
        }

        if (layout.WheelSizeBits > 0)
        {
            if (layout.WheelSigned) WriteSigned(report, baseBit + layout.WheelOffsetBits, layout.WheelSizeBits, wheel);
            else WriteUnsigned(report, baseBit + layout.WheelOffsetBits, layout.WheelSizeBits, wheel);
        }

        return report;
    }

    /// <summary>
    /// Builds a standard boot-protocol mouse report.
    /// </summary>
    /// <param name="buttons">The current mouse button bitmask.</param>
    /// <param name="dx">Relative X movement.</param>
    /// <param name="dy">Relative Y movement.</param>
    /// <param name="wheel">Relative wheel movement.</param>
    /// <param name="reportLen">The output report size, usually 3 or 4 bytes.</param>
    /// <returns>A serialized boot mouse report.</returns>
    public static byte[] BuildBootMouse(byte buttons, int dx, int dy, int wheel, int reportLen = 4)
    {
        if (reportLen is not 3 and not 4)
        {
            throw new ArgumentOutOfRangeException(nameof(reportLen), "Mouse report length must be 3 or 4.");
        }

        Span<byte> report = stackalloc byte[4];
        report[0] = buttons;
        report[1] = unchecked((byte)(sbyte)Math.Clamp(dx, -127, 127));
        report[2] = unchecked((byte)(sbyte)Math.Clamp(dy, -127, 127));
        report[3] = unchecked((byte)(sbyte)Math.Clamp(wheel, -127, 127));
        return report[..reportLen].ToArray();
    }

    /// <summary>
    /// Builds a keyboard report using a parsed layout when available, otherwise boot format.
    /// </summary>
    /// <param name="layout">The parsed keyboard layout, if known.</param>
    /// <param name="modifiers">The HID modifier bitmask.</param>
    /// <param name="keys">The key usages to include in the report.</param>
    /// <returns>A serialized keyboard report.</returns>
    public static byte[] BuildKeyboardReport(KeyboardLayoutInfo? layout, byte modifiers, params byte[] keys)
    {
        if (layout is null)
        {
            return BuildBootKeyboard(modifiers, keys);
        }

        return layout.Build(modifiers, keys);
    }

    /// <summary>
    /// Builds a standard boot-protocol keyboard report.
    /// </summary>
    /// <param name="modifiers">The HID modifier bitmask.</param>
    /// <param name="keys">The key usages to include in the report.</param>
    /// <returns>A serialized boot keyboard report.</returns>
    public static byte[] BuildBootKeyboard(byte modifiers, IReadOnlyList<byte> keys)
    {
        Span<byte> report = stackalloc byte[8];
        report[0] = modifiers;
        report[1] = 0;
        var count = Math.Min(6, keys.Count);
        for (var i = 0; i < count; i++)
        {
            report[2 + i] = keys[i];
        }
        return report.ToArray();
    }

    /// <summary>
    /// Maps one ASCII character to a HID usage and modifier set when possible.
    /// </summary>
    /// <param name="ch">The character to translate.</param>
    /// <param name="modifiers">Receives the required modifier bits.</param>
    /// <param name="usage">Receives the HID usage id.</param>
    /// <returns><c>true</c> when the character can be emitted through the basic mapping table.</returns>
    public static bool TryMapAsciiToHidKey(char ch, out byte modifiers, out byte usage)
    {
        modifiers = 0;
        usage = 0;
        const byte shift = 0x02;

        if (ch is >= 'a' and <= 'z') { usage = (byte)(0x04 + (ch - 'a')); return true; }
        if (ch is >= 'A' and <= 'Z') { usage = (byte)(0x04 + (ch - 'A')); modifiers = shift; return true; }
        if (ch is >= '1' and <= '9') { usage = (byte)(0x1E + (ch - '1')); return true; }
        if (ch == '0') { usage = 0x27; return true; }

        switch (ch)
        {
            case ' ': usage = 0x2C; return true;
            case '\n':
            case '\r': usage = 0x28; return true;
            case '\t': usage = 0x2B; return true;
            case '-': usage = 0x2D; return true;
            case '_': usage = 0x2D; modifiers = shift; return true;
            case '=': usage = 0x2E; return true;
            case '+': usage = 0x2E; modifiers = shift; return true;
            case '[': usage = 0x2F; return true;
            case '{': usage = 0x2F; modifiers = shift; return true;
            case ']': usage = 0x30; return true;
            case '}': usage = 0x30; modifiers = shift; return true;
            case '\\': usage = 0x31; return true;
            case '|': usage = 0x31; modifiers = shift; return true;
            case ';': usage = 0x33; return true;
            case ':': usage = 0x33; modifiers = shift; return true;
            case '\'': usage = 0x34; return true;
            case '"': usage = 0x34; modifiers = shift; return true;
            case ',': usage = 0x36; return true;
            case '<': usage = 0x36; modifiers = shift; return true;
            case '.': usage = 0x37; return true;
            case '>': usage = 0x37; modifiers = shift; return true;
            case '/': usage = 0x38; return true;
            case '?': usage = 0x38; modifiers = shift; return true;
            case '!': usage = 0x1E; modifiers = shift; return true;
            default: return false;
        }
    }

    private static void WriteBits(Span<byte> buffer, int bitOffset, int bitSize, int value)
    {
        if (bitSize <= 0) return;
        var data = unchecked((uint)value);
        for (var i = 0; i < bitSize; i++)
        {
            var bit = (int)((data >> i) & 1U);
            var dst = bitOffset + i;
            var dstByte = dst / 8;
            var dstBit = dst % 8;
            if (dstByte < 0 || dstByte >= buffer.Length) continue;
            if (bit != 0) buffer[dstByte] |= (byte)(1 << dstBit);
            else buffer[dstByte] &= (byte)~(1 << dstBit);
        }
    }

    private static void WriteSigned(Span<byte> buffer, int bitOffset, int bitSize, int value)
    {
        if (bitSize <= 0) return;
        var max = (1 << (bitSize - 1)) - 1;
        var min = -(1 << (bitSize - 1));
        var clamped = Math.Clamp(value, min, max);
        var encoded = clamped < 0 ? (1 << bitSize) + clamped : clamped;
        WriteBits(buffer, bitOffset, bitSize, encoded);
    }

    private static void WriteUnsigned(Span<byte> buffer, int bitOffset, int bitSize, int value)
    {
        if (bitSize <= 0) return;
        var max = (1 << bitSize) - 1;
        var clamped = Math.Clamp(value, 0, max);
        WriteBits(buffer, bitOffset, bitSize, clamped);
    }
}
