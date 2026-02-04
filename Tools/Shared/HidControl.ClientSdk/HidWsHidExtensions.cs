using System.Text.Json;

namespace HidControl.ClientSdk;

/// <summary>
/// Helper extensions for the server's low-level HID WebSocket endpoint (`/ws/hid`).
/// </summary>
public static class HidWsHidExtensions
{
    private static readonly JsonSerializerOptions DefaultJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Sends a keyboard shortcut chord (e.g. "Ctrl+Alt+Del") to the server over `/ws/hid`.
    /// </summary>
    /// <param name="ws">WS client.</param>
    /// <param name="shortcut">Shortcut chord string.</param>
    /// <param name="itfSel">Keyboard interface selector (optional).</param>
    /// <param name="holdMs">Delay between key-down and key-up.</param>
    /// <param name="applyMapping">True to apply per-device mapping.</param>
    /// <param name="id">Message id (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task SendKeyboardShortcutAsync(
        this HidControlWsClient ws,
        string shortcut,
        byte? itfSel = null,
        int holdMs = 80,
        bool applyMapping = true,
        string? id = null,
        CancellationToken ct = default)
    {
        var msg = new
        {
            type = "keyboard.shortcut",
            id,
            shortcut,
            itfSel,
            holdMs,
            applyMapping
        };
        return ws.SendJsonAsync(msg, DefaultJson, ct);
    }

    /// <summary>
    /// Sends a keyboard press event (down+up) for a single usage to the server over `/ws/hid`.
    /// </summary>
    /// <param name="ws">WS client.</param>
    /// <param name="usage">HID usage (0..255).</param>
    /// <param name="mods">Modifier bitmask (Ctrl=0x01, Shift=0x02, Alt=0x04, Win=0x08).</param>
    /// <param name="itfSel">Keyboard interface selector (optional).</param>
    /// <param name="id">Message id (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task SendKeyboardPressAsync(
        this HidControlWsClient ws,
        byte usage,
        byte mods = 0,
        byte? itfSel = null,
        string? id = null,
        CancellationToken ct = default)
    {
        var msg = new
        {
            type = "keyboard.press",
            id,
            usage,
            mods,
            itfSel
        };
        return ws.SendJsonAsync(msg, DefaultJson, ct);
    }

    /// <summary>
    /// Sends a text typing request to the server over `/ws/hid` (ASCII only on the server side).
    /// </summary>
    /// <param name="ws">WS client.</param>
    /// <param name="text">Text payload.</param>
    /// <param name="itfSel">Keyboard interface selector (optional).</param>
    /// <param name="id">Message id (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task SendKeyboardTextAsync(
        this HidControlWsClient ws,
        string text,
        string? layout = null,
        byte? itfSel = null,
        string? id = null,
        CancellationToken ct = default)
    {
        var msg = new
        {
            type = "keyboard.text",
            id,
            text,
            layout,
            itfSel
        };
        return ws.SendJsonAsync(msg, DefaultJson, ct);
    }

    /// <summary>
    /// Sends a relative mouse move to the server over `/ws/hid`.
    /// </summary>
    /// <param name="ws">WS client.</param>
    /// <param name="dx">Delta X.</param>
    /// <param name="dy">Delta Y.</param>
    /// <param name="itfSel">Mouse interface selector (optional).</param>
    /// <param name="id">Message id (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task SendMouseMoveAsync(
        this HidControlWsClient ws,
        int dx,
        int dy,
        byte? itfSel = null,
        string? id = null,
        CancellationToken ct = default)
    {
        var msg = new
        {
            type = "mouse.move",
            id,
            dx,
            dy,
            itfSel
        };
        return ws.SendJsonAsync(msg, DefaultJson, ct);
    }

    /// <summary>
    /// Sends a mouse button change to the server over `/ws/hid`.
    /// </summary>
    /// <param name="ws">WS client.</param>
    /// <param name="button">Button name: left/right/middle/back/forward.</param>
    /// <param name="down">True for down, false for up.</param>
    /// <param name="itfSel">Mouse interface selector (optional).</param>
    /// <param name="id">Message id (optional).</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task SendMouseButtonAsync(
        this HidControlWsClient ws,
        string button,
        bool down,
        byte? itfSel = null,
        string? id = null,
        CancellationToken ct = default)
    {
        var msg = new
        {
            type = "mouse.button",
            id,
            button,
            down,
            itfSel
        };
        return ws.SendJsonAsync(msg, DefaultJson, ct);
    }
}
