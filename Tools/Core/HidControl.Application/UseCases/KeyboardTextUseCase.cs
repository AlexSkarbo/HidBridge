using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Types a text string on the controlled device by mapping characters into HID key presses.
/// </summary>
public sealed class KeyboardTextUseCase
{
    private readonly IKeyboardControl _keyboard;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public KeyboardTextUseCase(IKeyboardControl keyboard)
    {
        _keyboard = keyboard;
    }

    /// <summary>
    /// Executes the use case (ASCII-only by default for REST).
    /// </summary>
    public Task<KeyboardTextResult> ExecuteAsync(KeyboardTypeRequest req, CancellationToken ct)
    {
        return _keyboard.TypeTextAsync(req.Text ?? string.Empty, layout: null, itfSel: req.ItfSel, ct);
    }

    /// <summary>
    /// Executes the use case with an explicit layout (for WS).
    /// </summary>
    public Task<KeyboardTextResult> ExecuteAsync(string text, string? layout, byte? itfSel, CancellationToken ct)
    {
        return _keyboard.TypeTextAsync(text ?? string.Empty, layout, itfSel, ct);
    }
}

