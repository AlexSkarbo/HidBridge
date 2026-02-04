using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sends a mouse wheel delta to the controlled device.
/// </summary>
public sealed class MouseWheelUseCase
{
    private readonly IMouseControl _mouse;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public MouseWheelUseCase(IMouseControl mouse)
    {
        _mouse = mouse;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<MouseWheelResult> ExecuteAsync(MouseWheelRequest req, CancellationToken ct)
    {
        return _mouse.WheelAsync(req.Delta, req.ItfSel, ct);
    }
}

