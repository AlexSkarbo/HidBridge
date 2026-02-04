using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sends a relative mouse move to the controlled device.
/// </summary>
public sealed class MouseMoveUseCase
{
    private readonly IMouseControl _mouse;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public MouseMoveUseCase(IMouseControl mouse)
    {
        _mouse = mouse;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<MouseMoveResult> ExecuteAsync(MouseMoveRequest req, CancellationToken ct)
    {
        return _mouse.MoveAsync(req.Dx, req.Dy, req.Wheel ?? 0, req.ItfSel, ct);
    }
}

