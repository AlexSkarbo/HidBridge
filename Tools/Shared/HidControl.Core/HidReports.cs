namespace HidControl.Core;

// Builds HID input reports from parsed layouts.
/// <summary>
/// Core model for HidReports.
/// </summary>
public static class HidReports
{
    /// <summary>
    /// Tries to map a character into a HID key press for a given keyboard layout.
    /// Notes:
    /// - HID usages represent physical keys; for non-ASCII layouts (ru/uk) the target OS must have that layout active.
    /// - Layout values: "ascii" (default), "ru", "uk".
    /// </summary>
    /// <param name="ch">Input character.</param>
    /// <param name="layout">Layout name.</param>
    /// <param name="modifiers">Modifier bitmask (Shift=0x02).</param>
    /// <param name="usage">HID usage (0..255).</param>
    /// <returns>True if mapped.</returns>
    public static bool TryMapTextToHidKey(char ch, string? layout, out byte modifiers, out byte usage)
    {
        // Always allow ASCII mapping regardless of requested layout.
        if (TryMapAsciiToHidKey(ch, out modifiers, out usage))
        {
            return true;
        }

        string lay = string.IsNullOrWhiteSpace(layout) ? "ascii" : layout.Trim().ToLowerInvariant();
        switch (lay)
        {
            case "ru":
            case "ru-ru":
            case "russian":
                return TryMapRussianToHidKey(ch, out modifiers, out usage);
            case "uk":
            case "uk-ua":
            case "ua":
            case "ukrainian":
                return TryMapUkrainianToHidKey(ch, out modifiers, out usage);
            default:
                modifiers = 0;
                usage = 0;
                return false;
        }
    }

    /// <summary>
    /// Builds mouse from layout.
    /// </summary>
    /// <param name="layout">The layout.</param>
    /// <param name="buttons">The buttons.</param>
    /// <param name="dx">The dx.</param>
    /// <param name="dy">The dy.</param>
    /// <param name="wheel">The wheel.</param>
    /// <returns>Result.</returns>
    public static byte[] BuildMouseFromLayout(MouseReportLayout layout, byte buttons, int dx, int dy, int wheel)
    {
        int totalBits = layout.TotalBits;
        int reportBytes = (totalBits + 7) / 8;
        int prefixBytes = layout.ReportId == 0 ? 0 : 1;
        byte[] report = new byte[prefixBytes + reportBytes];

        int baseBit = prefixBytes * 8;
        if (layout.ReportId != 0)
        {
            report[0] = layout.ReportId;
        }

        foreach (HidField field in layout.Fields)
        {
            if (field.UsagePage == 0x09)
            {
                int idx = field.Usage - 1;
                if (idx >= 0 && idx < 8)
                {
                    bool down = (buttons & (1 << idx)) != 0;
                    WriteBits(report, baseBit + field.BitOffset, field.BitSize, down ? 1 : 0);
                }
            }
            else if (field.UsagePage == 0x01 && field.Usage == 0x30)
            {
                WriteSigned(report, baseBit + field.BitOffset, field.BitSize, dx);
            }
            else if (field.UsagePage == 0x01 && field.Usage == 0x31)
            {
                WriteSigned(report, baseBit + field.BitOffset, field.BitSize, dy);
            }
            else if (field.UsagePage == 0x01 && field.Usage == 0x38)
            {
                WriteSigned(report, baseBit + field.BitOffset, field.BitSize, wheel);
            }
        }

        return report;
    }

    /// <summary>
    /// Builds mouse from layout info.
    /// </summary>
    /// <param name="layout">The layout.</param>
    /// <param name="buttons">The buttons.</param>
    /// <param name="dx">The dx.</param>
    /// <param name="dy">The dy.</param>
    /// <param name="wheel">The wheel.</param>
    /// <returns>Result.</returns>
    public static byte[] BuildMouseFromLayoutInfo(MouseLayoutInfo layout, byte buttons, int dx, int dy, int wheel)
    {
        int buttonsBits = layout.ButtonsCount * layout.ButtonsSizeBits;
        int maxBit = 0;
        if (buttonsBits > 0)
        {
            maxBit = Math.Max(maxBit, layout.ButtonsOffsetBits + buttonsBits);
        }
        if (layout.XSizeBits > 0)
        {
            maxBit = Math.Max(maxBit, layout.XOffsetBits + layout.XSizeBits);
        }
        if (layout.YSizeBits > 0)
        {
            maxBit = Math.Max(maxBit, layout.YOffsetBits + layout.YSizeBits);
        }
        if (layout.WheelSizeBits > 0)
        {
            maxBit = Math.Max(maxBit, layout.WheelOffsetBits + layout.WheelSizeBits);
        }

        int reportBytes = (maxBit + 7) / 8;
        int prefixBytes = layout.ReportId == 0 ? 0 : 1;
        byte[] report = new byte[prefixBytes + reportBytes];
        int baseBit = prefixBytes * 8;
        if (layout.ReportId != 0)
        {
            report[0] = layout.ReportId;
        }

        if (buttonsBits > 0 && layout.ButtonsSizeBits > 0)
        {
            int count = Math.Min((int)layout.ButtonsCount, 8);
            for (int i = 0; i < count; i++)
            {
                bool down = (buttons & (1 << i)) != 0;
                int bitOffset = baseBit + layout.ButtonsOffsetBits + (i * layout.ButtonsSizeBits);
                WriteBits(report, bitOffset, layout.ButtonsSizeBits, down ? 1 : 0);
            }
        }
        if (layout.XSizeBits > 0)
        {
            WriteSigned(report, baseBit + layout.XOffsetBits, layout.XSizeBits, dx);
        }
        if (layout.YSizeBits > 0)
        {
            WriteSigned(report, baseBit + layout.YOffsetBits, layout.YSizeBits, dy);
        }
        if (layout.WheelSizeBits > 0)
        {
            WriteSigned(report, baseBit + layout.WheelOffsetBits, layout.WheelSizeBits, wheel);
        }

        return report;
    }

    /// <summary>
    /// Builds boot mouse.
    /// </summary>
    /// <param name="buttons">The buttons.</param>
    /// <param name="dx">The dx.</param>
    /// <param name="dy">The dy.</param>
    /// <param name="wheel">The wheel.</param>
    /// <param name="reportLen">The reportLen.</param>
    /// <returns>Result.</returns>
    public static byte[] BuildBootMouse(byte buttons, int dx, int dy, int wheel, int reportLen)
    {
        if (reportLen != 3 && reportLen != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(reportLen), "Mouse report length must be 3 or 4.");
        }

        Span<byte> report = stackalloc byte[4];
        report[0] = buttons;
        report[1] = unchecked((byte)(sbyte)ClampToSbyte(dx));
        report[2] = unchecked((byte)(sbyte)ClampToSbyte(dy));
        report[3] = unchecked((byte)(sbyte)ClampToSbyte(wheel));

        return report.Slice(0, reportLen).ToArray();
    }

    /// <summary>
    /// Builds boot keyboard.
    /// </summary>
    /// <param name="modifiers">The modifiers.</param>
    /// <param name="keys">The keys.</param>
    /// <returns>Result.</returns>
    public static byte[] BuildBootKeyboard(byte modifiers, IReadOnlyList<byte> keys)
    {
        Span<byte> report = stackalloc byte[8];
        report[0] = modifiers;
        report[1] = 0;

        int count = Math.Min(6, keys.Count);
        for (int i = 0; i < count; i++)
        {
            report[2 + i] = keys[i];
        }

        return report.ToArray();
    }

    /// <summary>
    /// Tries to map ascii to hid key.
    /// </summary>
    /// <param name="ch">The ch.</param>
    /// <param name="modifiers">The modifiers.</param>
    /// <param name="usage">The usage.</param>
    /// <returns>Result.</returns>
    public static bool TryMapAsciiToHidKey(char ch, out byte modifiers, out byte usage)
    {
        modifiers = 0;
        usage = 0;

        const byte Shift = 0x02;

        if (ch is >= 'a' and <= 'z')
        {
            usage = (byte)(0x04 + (ch - 'a'));
            return true;
        }

        if (ch is >= 'A' and <= 'Z')
        {
            modifiers = Shift;
            usage = (byte)(0x04 + (ch - 'A'));
            return true;
        }

        if (ch is >= '1' and <= '9')
        {
            usage = (byte)(0x1E + (ch - '1'));
            return true;
        }

        if (ch == '0')
        {
            usage = 0x27;
            return true;
        }

        switch (ch)
        {
            case '\n':
            case '\r':
                usage = 0x28;
                return true;
            case '\t':
                usage = 0x2B;
                return true;
            case ' ':
                usage = 0x2C;
                return true;
            case '-':
                usage = 0x2D;
                return true;
            case '=':
                usage = 0x2E;
                return true;
            case '[':
                usage = 0x2F;
                return true;
            case ']':
                usage = 0x30;
                return true;
            case '\\':
                usage = 0x31;
                return true;
            case ';':
                usage = 0x33;
                return true;
            case '\'':
                usage = 0x34;
                return true;
            case '`':
                usage = 0x35;
                return true;
            case ',':
                usage = 0x36;
                return true;
            case '.':
                usage = 0x37;
                return true;
            case '/':
                usage = 0x38;
                return true;

            case '!':
                modifiers = Shift;
                usage = 0x1E;
                return true;
            case '@':
                modifiers = Shift;
                usage = 0x1F;
                return true;
            case '#':
                modifiers = Shift;
                usage = 0x20;
                return true;
            case '$':
                modifiers = Shift;
                usage = 0x21;
                return true;
            case '%':
                modifiers = Shift;
                usage = 0x22;
                return true;
            case '^':
                modifiers = Shift;
                usage = 0x23;
                return true;
            case '&':
                modifiers = Shift;
                usage = 0x24;
                return true;
            case '*':
                modifiers = Shift;
                usage = 0x25;
                return true;
            case '(':
                modifiers = Shift;
                usage = 0x26;
                return true;
            case ')':
                modifiers = Shift;
                usage = 0x27;
                return true;

            case '_':
                modifiers = Shift;
                usage = 0x2D;
                return true;
            case '+':
                modifiers = Shift;
                usage = 0x2E;
                return true;
            case '{':
                modifiers = Shift;
                usage = 0x2F;
                return true;
            case '}':
                modifiers = Shift;
                usage = 0x30;
                return true;
            case '|':
                modifiers = Shift;
                usage = 0x31;
                return true;
            case ':':
                modifiers = Shift;
                usage = 0x33;
                return true;
            case '"':
                modifiers = Shift;
                usage = 0x34;
                return true;
            case '~':
                modifiers = Shift;
                usage = 0x35;
                return true;
            case '<':
                modifiers = Shift;
                usage = 0x36;
                return true;
            case '>':
                modifiers = Shift;
                usage = 0x37;
                return true;
            case '?':
                modifiers = Shift;
                usage = 0x38;
                return true;
        }

        return false;
    }

    private static bool TryMapRussianToHidKey(char ch, out byte modifiers, out byte usage)
    {
        const byte Shift = 0x02;
        bool isUpper = char.IsUpper(ch);
        char c = char.ToLowerInvariant(ch);

        // Standard RU layout (JCUKEN) mapped onto US physical keys (HID usages).
        // q w e r t y u i o p [ ] ` ; ' , . /
        usage = c switch
        {
            'ё' => 0x35, // `~
            'й' => 0x14, // Q
            'ц' => 0x1A, // W
            'у' => 0x08, // E
            'к' => 0x15, // R
            'е' => 0x17, // T
            'н' => 0x1C, // Y
            'г' => 0x18, // U
            'ш' => 0x0C, // I
            'щ' => 0x12, // O
            'з' => 0x13, // P
            'х' => 0x2F, // [
            'ъ' => 0x30, // ]

            'ф' => 0x04, // A
            'ы' => 0x16, // S
            'в' => 0x07, // D
            'а' => 0x09, // F
            'п' => 0x0A, // G
            'р' => 0x0B, // H
            'о' => 0x0D, // J
            'л' => 0x0E, // K
            'д' => 0x0F, // L
            'ж' => 0x33, // ;
            'э' => 0x34, // '

            'я' => 0x1D, // Z
            'ч' => 0x1B, // X
            'с' => 0x06, // C
            'м' => 0x19, // V
            'и' => 0x05, // B
            'т' => 0x11, // N
            'ь' => 0x10, // M
            'б' => 0x36, // ,
            'ю' => 0x37, // .

            _ => (byte)0
        };

        if (usage == 0)
        {
            modifiers = 0;
            return false;
        }

        modifiers = isUpper ? Shift : (byte)0;
        return true;
    }

    private static bool TryMapUkrainianToHidKey(char ch, out byte modifiers, out byte usage)
    {
        const byte Shift = 0x02;
        bool isUpper = char.IsUpper(ch);
        char c = char.ToLowerInvariant(ch);

        // Standard UA layout (similar to RU, with extra letters).
        usage = c switch
        {
            'ґ' => 0x35, // `~
            'й' => 0x14, // Q
            'ц' => 0x1A, // W
            'у' => 0x08, // E
            'к' => 0x15, // R
            'е' => 0x17, // T
            'н' => 0x1C, // Y
            'г' => 0x18, // U
            'ш' => 0x0C, // I
            'щ' => 0x12, // O
            'з' => 0x13, // P
            'х' => 0x2F, // [
            'ї' => 0x30, // ]

            'ф' => 0x04, // A
            'і' => 0x16, // S
            'в' => 0x07, // D
            'а' => 0x09, // F
            'п' => 0x0A, // G
            'р' => 0x0B, // H
            'о' => 0x0D, // J
            'л' => 0x0E, // K
            'д' => 0x0F, // L
            'ж' => 0x33, // ;
            'є' => 0x34, // '

            'я' => 0x1D, // Z
            'ч' => 0x1B, // X
            'с' => 0x06, // C
            'м' => 0x19, // V
            'и' => 0x05, // B
            'т' => 0x11, // N
            'ь' => 0x10, // M
            'б' => 0x36, // ,
            'ю' => 0x37, // .

            _ => (byte)0
        };

        if (usage == 0)
        {
            modifiers = 0;
            return false;
        }

        modifiers = isUpper ? Shift : (byte)0;
        return true;
    }

    /// <summary>
    /// Executes ClampToSbyte.
    /// </summary>
    /// <param name="v">The v.</param>
    /// <returns>Result.</returns>
    private static int ClampToSbyte(int v)
    {
        if (v > sbyte.MaxValue) return sbyte.MaxValue;
        if (v < sbyte.MinValue) return sbyte.MinValue;
        return v;
    }

    /// <summary>
    /// Writes signed.
    /// </summary>
    /// <param name="buf">The buf.</param>
    /// <param name="bitOffset">The bitOffset.</param>
    /// <param name="bitSize">The bitSize.</param>
    /// <param name="value">The value.</param>
    private static void WriteSigned(byte[] buf, int bitOffset, int bitSize, int value)
    {
        if (bitSize <= 0 || bitSize > 32) return;
        int max = (1 << (bitSize - 1)) - 1;
        int min = -(1 << (bitSize - 1));
        if (value > max) value = max;
        if (value < min) value = min;

        uint mask = (uint)((1L << bitSize) - 1);
        uint uvalue = (uint)(value & (int)mask);
        WriteBits(buf, bitOffset, bitSize, (int)uvalue);
    }

    /// <summary>
    /// Writes bits.
    /// </summary>
    /// <param name="buf">The buf.</param>
    /// <param name="bitOffset">The bitOffset.</param>
    /// <param name="bitSize">The bitSize.</param>
    /// <param name="value">The value.</param>
    private static void WriteBits(byte[] buf, int bitOffset, int bitSize, int value)
    {
        for (int i = 0; i < bitSize; i++)
        {
            int bit = (value >> i) & 1;
            int idx = bitOffset + i;
            int byteIndex = idx / 8;
            int bitIndex = idx % 8;
            if (bit != 0)
            {
                buf[byteIndex] |= (byte)(1 << bitIndex);
            }
            else
            {
                buf[byteIndex] &= (byte)~(1 << bitIndex);
            }
        }
    }
}
