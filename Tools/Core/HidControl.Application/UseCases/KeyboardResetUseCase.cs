using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Clears server-side keyboard state and emits an "all up" keyboard report.
/// </summary>
public sealed class KeyboardResetUseCase
{
    private readonly IKeyboardControl _keyboard;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public KeyboardResetUseCase(IKeyboardControl keyboard)
    {
        _keyboard = keyboard;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<KeyboardStateChangeResult> ExecuteAsync(KeyboardResetRequest req, CancellationToken ct)
    {
        return _keyboard.ResetAsync(req.ItfSel, ct);
    }
}

