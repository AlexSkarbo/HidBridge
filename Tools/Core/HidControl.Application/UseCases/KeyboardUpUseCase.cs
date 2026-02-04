using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sets a key (or modifier) to the "up" state and emits a keyboard report.
/// </summary>
public sealed class KeyboardUpUseCase
{
    private readonly IKeyboardControl _keyboard;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public KeyboardUpUseCase(IKeyboardControl keyboard)
    {
        _keyboard = keyboard;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<KeyboardStateChangeResult> ExecuteAsync(KeyboardPressRequest req, CancellationToken ct)
    {
        return _keyboard.KeyUpAsync(req.Usage, req.Modifiers, req.ItfSel, ct);
    }
}

