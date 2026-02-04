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
}

