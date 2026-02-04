using System.Globalization;

namespace HidControl.Core;

/// <summary>
/// Helper for parsing human-friendly keyboard shortcut chords (e.g. "Ctrl+Alt+Del") into HID usages.
/// </summary>
public static class HidKeyboardShortcuts
{
    /// <summary>
    /// Parses a shortcut chord into a HID keyboard modifier mask and up to 6 key usages.
    /// </summary>
    /// <param name="shortcut">Shortcut chord (e.g. "Ctrl+Alt+Del", "Win+R", "Alt+F4").</param>
    /// <param name="modifiers">Resulting HID modifier mask (bits map to 0xE0..0xE7).</param>
    /// <param name="keys">Resulting non-modifier key usages (max 6).</param>
    /// <param name="error">Error string (if parsing fails).</param>
    /// <returns>True if parsed successfully.</returns>
    public static bool TryParseChord(string shortcut, out byte modifiers, out byte[] keys, out string? error)
    {
        modifiers = 0;
        keys = Array.Empty<byte>();
        error = null;

        if (string.IsNullOrWhiteSpace(shortcut))
        {
            error = "shortcut is required";
            return false;
        }

        string trimmed = shortcut.Trim();
        char separator = trimmed.Contains('+')
            ? '+'
            : (trimmed.Contains('-') && trimmed != "-" ? '-' : '+');

        string[] parts = trimmed.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "shortcut has no keys";
            return false;
        }

        var list = new List<byte>(Math.Min(6, parts.Length));

        foreach (string part in parts)
        {
            if (!TryParseToken(part, out byte usage, out byte impliedMods, out string? tokenErr))
            {
                error = tokenErr ?? $"unknown key '{part}'";
                return false;
            }

            modifiers |= impliedMods;
            if (IsModifierUsage(usage))
            {
                modifiers |= ModifierBitFromUsage(usage);
                continue;
            }

            if (!list.Contains(usage))
            {
                list.Add(usage);
                if (list.Count > 6)
                {
                    error = "too many non-modifier keys (max 6)";
                    return false;
                }
            }
        }

        keys = list.ToArray();
        return true;
    }

    private static bool TryParseToken(string token, out byte usage, out byte impliedMods, out string? error)
    {
        usage = 0;
        impliedMods = 0;
        error = null;

        string raw = token.Trim();
        if (raw.Length == 0)
        {
            error = "empty key token";
            return false;
        }

        if (TryParseUsageNumber(raw, out usage))
        {
            return true;
        }

        if (raw.Length == 1)
        {
            char ch = raw[0];
            if (ch is >= 'a' and <= 'z')
            {
                usage = (byte)(0x04 + (ch - 'a'));
                return true;
            }
            if (ch is >= 'A' and <= 'Z')
            {
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

            if (HidReports.TryMapAsciiToHidKey(ch, out byte mods, out byte mappedUsage))
            {
                impliedMods = mods;
                usage = mappedUsage;
                return true;
            }
        }

        string key = Normalize(raw);

        // Function keys (F1..F24)
        if (key.Length >= 2 && (key[0] == 'f' || key[0] == 'F'))
        {
            if (int.TryParse(key.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
            {
                usage = (byte)(0x3A + (f - 1));
                return true;
            }
        }

        // KeyboardEvent.code patterns: KeyA..KeyZ, Digit0..Digit9
        if (key.Length == 4 && key.StartsWith("key", StringComparison.OrdinalIgnoreCase))
        {
            char ch = key[3];
            if (ch is >= 'a' and <= 'z') { usage = (byte)(0x04 + (ch - 'a')); return true; }
            if (ch is >= 'A' and <= 'Z') { usage = (byte)(0x04 + (ch - 'A')); return true; }
        }
        if (key.Length == 6 && key.StartsWith("digit", StringComparison.OrdinalIgnoreCase))
        {
            char d = key[5];
            if (d is >= '1' and <= '9') { usage = (byte)(0x1E + (d - '1')); return true; }
            if (d == '0') { usage = 0x27; return true; }
        }

        if (NameToUsage.TryGetValue(key, out usage))
        {
            return true;
        }

        error = $"unknown key '{token}'";
        return false;
    }

    private static bool TryParseUsageNumber(string token, out byte usage)
    {
        usage = 0;

        string s = token.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return byte.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out usage);
        }

        return byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out usage);
    }

    private static string Normalize(string token)
        => token.Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Trim();

    private static bool IsModifierUsage(byte usage) => usage >= 0xE0 && usage <= 0xE7;

    private static byte ModifierBitFromUsage(byte usage) => (byte)(1 << (usage - 0xE0));

    private static readonly Dictionary<string, byte> NameToUsage = new(StringComparer.OrdinalIgnoreCase)
    {
        // Modifiers (default to "left" variants).
        ["ctrl"] = 0xE0,
        ["control"] = 0xE0,
        ["lctrl"] = 0xE0,
        ["leftctrl"] = 0xE0,
        ["rctrl"] = 0xE4,
        ["rightctrl"] = 0xE4,

        ["shift"] = 0xE1,
        ["lshift"] = 0xE1,
        ["leftshift"] = 0xE1,
        ["rshift"] = 0xE5,
        ["rightshift"] = 0xE5,

        ["alt"] = 0xE2,
        ["lalt"] = 0xE2,
        ["leftalt"] = 0xE2,
        ["ralt"] = 0xE6,
        ["rightalt"] = 0xE6,
        ["option"] = 0xE2,

        ["win"] = 0xE3,
        ["gui"] = 0xE3,
        ["meta"] = 0xE3,
        ["command"] = 0xE3,
        ["cmd"] = 0xE3,

        // KeyboardEvent.code modifier names.
        ["controlleft"] = 0xE0,
        ["controlright"] = 0xE4,
        ["shiftleft"] = 0xE1,
        ["shiftright"] = 0xE5,
        ["altleft"] = 0xE2,
        ["altright"] = 0xE6,
        ["metaleft"] = 0xE3,
        ["metaright"] = 0xE7,

        // Common keys.
        ["enter"] = 0x28,
        ["return"] = 0x28,
        ["esc"] = 0x29,
        ["escape"] = 0x29,
        ["tab"] = 0x2B,
        ["space"] = 0x2C,
        ["spacebar"] = 0x2C,
        ["backspace"] = 0x2A,
        ["bs"] = 0x2A,

        ["capslock"] = 0x39,
        ["caps"] = 0x39,

        ["insert"] = 0x49,
        ["ins"] = 0x49,
        ["delete"] = 0x4C,
        ["del"] = 0x4C,
        ["home"] = 0x4A,
        ["end"] = 0x4D,
        ["pageup"] = 0x4B,
        ["pgup"] = 0x4B,
        ["pagedown"] = 0x4E,
        ["pgdown"] = 0x4E,
        ["pgdn"] = 0x4E,

        ["up"] = 0x52,
        ["arrowup"] = 0x52,
        ["down"] = 0x51,
        ["arrowdown"] = 0x51,
        ["left"] = 0x50,
        ["arrowleft"] = 0x50,
        ["right"] = 0x4F,
        ["arrowright"] = 0x4F,

        ["printscreen"] = 0x46,
        ["prtsc"] = 0x46,
        ["scrolllock"] = 0x47,
        ["pause"] = 0x48,
        ["break"] = 0x48,
        ["numlock"] = 0x53,

        ["menu"] = 0x65,
        ["contextmenu"] = 0x65,
        ["application"] = 0x65,

        // Symbol keys by name (KeyboardEvent.code-ish).
        ["minus"] = 0x2D,
        ["equal"] = 0x2E,
        ["backquote"] = 0x35,
        ["bracketleft"] = 0x2F,
        ["bracketright"] = 0x30,
        ["backslash"] = 0x31,
        ["semicolon"] = 0x33,
        ["quote"] = 0x34,
        ["comma"] = 0x36,
        ["period"] = 0x37,
        ["slash"] = 0x38,

        // Named key (KeyboardEvent.code).
        ["backspacekey"] = 0x2A,
    };
}
