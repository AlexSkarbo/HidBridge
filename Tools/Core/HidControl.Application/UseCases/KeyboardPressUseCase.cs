using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sends a single key press (down+up) to the controlled device.
/// </summary>
public sealed class KeyboardPressUseCase
{
    private readonly IKeyboardControl _keyboard;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public KeyboardPressUseCase(IKeyboardControl keyboard)
    {
        _keyboard = keyboard;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<KeyboardPressResult> ExecuteAsync(KeyboardPressRequest req, CancellationToken ct)
    {
        byte modifiers = req.Modifiers ?? 0;
        return _keyboard.PressAsync(req.Usage, modifiers, req.ItfSel, ct);
    }
}

