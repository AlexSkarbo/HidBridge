using HidControl.Application.Models;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Application port for injecting keyboard input into the controlled device.
/// </summary>
public interface IKeyboardControl
{
    /// <summary>
    /// Presses (down+up) a single HID keyboard usage with optional modifiers.
    /// </summary>
    Task<KeyboardPressResult> PressAsync(byte usage, byte modifiers, byte? itfSel, CancellationToken ct);

    /// <summary>
    /// Types a text string by mapping characters into HID key presses.
    /// </summary>
    Task<KeyboardTextResult> TypeTextAsync(string text, string? layout, byte? itfSel, CancellationToken ct);

    /// <summary>
    /// Sends a shortcut chord (e.g. Ctrl+Alt+Del) as key-down, optional hold, and key-up.
    /// </summary>
    Task<KeyboardShortcutResult> SendShortcutAsync(string shortcut, int holdMs, bool applyMapping, byte? itfSel, CancellationToken ct);

    /// <summary>
    /// Sets a key (or modifier) to the "down" state, updates server-side keyboard state, and emits a report.
    /// </summary>
    Task<KeyboardStateChangeResult> KeyDownAsync(byte usage, byte? modifiers, byte? itfSel, CancellationToken ct);

    /// <summary>
    /// Sets a key (or modifier) to the "up" state, updates server-side keyboard state, and emits a report.
    /// </summary>
    Task<KeyboardStateChangeResult> KeyUpAsync(byte usage, byte? modifiers, byte? itfSel, CancellationToken ct);

    /// <summary>
    /// Sends a raw keyboard report (modifiers + up to 6 keys), optionally applying per-device mapping.
    /// </summary>
    Task<KeyboardReportResult> SendReportAsync(byte modifiers, IReadOnlyList<byte> keys, bool applyMapping, byte? itfSel, CancellationToken ct);

    /// <summary>
    /// Clears server-side keyboard state and emits an "all up" report.
    /// </summary>
    Task<KeyboardStateChangeResult> ResetAsync(byte? itfSel, CancellationToken ct);
}
