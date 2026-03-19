using System.Text.Json;

namespace HidBridge.Transport.Uart;

/// <summary>
/// Executes normalized HID command actions through <see cref="HidBridgeUartClient"/>.
/// </summary>
public static class HidBridgeUartCommandDispatcher
{
    /// <summary>
    /// Executes one HID action request against UART bridge transport.
    /// </summary>
    public static async Task ExecuteAsync(
        HidBridgeUartClient client,
        string action,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var normalizedAction = NormalizeAction(action);
        switch (normalizedAction)
        {
            case "noop":
                return;
            case "mouse.move":
                await client.SendMouseMoveAsync(
                    GetInt(args, "dx"),
                    GetInt(args, "dy"),
                    GetInt(args, "wheel", 0),
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                return;
            case "mouse.wheel":
                await client.SendMouseMoveAsync(
                    0,
                    0,
                    GetInt(args, "delta"),
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                return;
            case "mouse.button":
                await client.SetMouseButtonAsync(
                    GetString(args, "button"),
                    GetBool(args, "down"),
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                return;
            case "mouse.click":
                var clickButton = GetStringOrDefault(args, "button", "left");
                var clickHoldMs = Math.Max(0, GetInt(args, "holdMs", 25));
                await client.SetMouseButtonAsync(
                    clickButton,
                    down: true,
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                if (clickHoldMs > 0)
                {
                    await Task.Delay(clickHoldMs, cancellationToken);
                }

                await client.SetMouseButtonAsync(
                    clickButton,
                    down: false,
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                return;
            case "mouse.buttons":
                await client.SetMouseButtonsMaskAsync(
                    GetByte(args, "mask"),
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                return;
            case "keyboard.text":
                await client.SendKeyboardTextAsync(
                    GetString(args, "text"),
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                return;
            case "keyboard.press":
                await client.SendKeyboardPressAsync(
                    GetByte(args, "usage"),
                    GetByte(args, "modifiers", 0),
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                return;
            case "keyboard.shortcut":
                await client.SendKeyboardShortcutAsync(
                    GetString(args, "shortcut"),
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                return;
            case "keyboard.reset":
                await client.SendKeyboardResetAsync(
                    cancellationToken,
                    GetByteOrNull(args, "itfSel"));
                return;
            default:
                throw new InvalidOperationException($"Unsupported HID action '{action}'.");
        }
    }

    /// <summary>
    /// Normalizes legacy action aliases to dot-separated lowercase form.
    /// </summary>
    public static string NormalizeAction(string action)
    {
        return action.Trim().Replace('_', '.').ToLowerInvariant();
    }

    /// <summary>
    /// Reads required string argument from command payload.
    /// </summary>
    private static string GetString(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            throw new InvalidOperationException($"Missing args.{name}");
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Reads optional string argument from command payload.
    /// </summary>
    private static string GetStringOrDefault(IReadOnlyDictionary<string, object?> args, string name, string defaultValue)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? defaultValue,
            _ => value.ToString() ?? defaultValue,
        };
    }

    /// <summary>
    /// Reads boolean argument with tolerant parsing for JSON and string tokens.
    /// </summary>
    private static bool GetBool(IReadOnlyDictionary<string, object?> args, string name, bool defaultValue = false)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var number) => number != 0,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    /// <summary>
    /// Reads integer argument with tolerant parsing for JSON and string tokens.
    /// </summary>
    private static int GetInt(IReadOnlyDictionary<string, object?> args, string name, int defaultValue = 0)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            int number => number,
            long number => checked((int)number),
            byte number => number,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var number) => number,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    /// <summary>
    /// Reads byte argument and clamps out-of-range values.
    /// </summary>
    private static byte GetByte(IReadOnlyDictionary<string, object?> args, string name, byte defaultValue = 0)
    {
        return (byte)Math.Clamp(GetInt(args, name, defaultValue), byte.MinValue, byte.MaxValue);
    }

    /// <summary>
    /// Reads optional byte argument.
    /// </summary>
    private static byte? GetByteOrNull(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.ContainsKey(name))
        {
            return null;
        }

        return GetByte(args, name);
    }
}
