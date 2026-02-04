using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sends a shortcut chord (e.g. "Ctrl+Alt+Del") to the controlled device.
/// </summary>
public sealed class KeyboardShortcutUseCase
{
    private readonly IKeyboardControl _keyboard;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public KeyboardShortcutUseCase(IKeyboardControl keyboard)
    {
        _keyboard = keyboard;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<KeyboardShortcutResult> ExecuteAsync(KeyboardShortcutRequest req, CancellationToken ct)
    {
        int holdMs = req.HoldMs ?? 80;
        bool applyMapping = req.ApplyMapping ?? true;
        return _keyboard.SendShortcutAsync(req.Shortcut, holdMs, applyMapping, req.ItfSel, ct);
    }

    /// <summary>
    /// Executes the use case from raw WS fields.
    /// </summary>
    public Task<KeyboardShortcutResult> ExecuteAsync(string shortcut, int holdMs, bool applyMapping, byte? itfSel, CancellationToken ct)
    {
        return _keyboard.SendShortcutAsync(shortcut, holdMs, applyMapping, itfSel, ct);
    }
}

