using HidControl.Application.Models;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Application port for injecting mouse input into the controlled device.
/// </summary>
public interface IMouseControl
{
    /// <summary>
    /// Sends a relative mouse move (and optional wheel delta).
    /// </summary>
    Task<MouseMoveResult> MoveAsync(int dx, int dy, int wheel, byte? itfSel, CancellationToken ct);

    /// <summary>
    /// Sends a mouse wheel delta.
    /// </summary>
    Task<MouseWheelResult> WheelAsync(int delta, byte? itfSel, CancellationToken ct);

    /// <summary>
    /// Sets a single named mouse button state (down/up).
    /// </summary>
    Task<MouseButtonResult> SetButtonAsync(string button, bool down, byte? itfSel, CancellationToken ct);

    /// <summary>
    /// Sets the mouse button mask directly.
    /// </summary>
    Task<MouseButtonsMaskResult> SetButtonsMaskAsync(byte mask, byte? itfSel, CancellationToken ct);
}

