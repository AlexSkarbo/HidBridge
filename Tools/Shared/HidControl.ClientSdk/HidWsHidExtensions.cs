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
}

