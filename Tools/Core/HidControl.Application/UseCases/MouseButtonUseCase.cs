using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sets a named mouse button state on the controlled device.
/// </summary>
public sealed class MouseButtonUseCase
{
    private readonly IMouseControl _mouse;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public MouseButtonUseCase(IMouseControl mouse)
    {
        _mouse = mouse;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<MouseButtonResult> ExecuteAsync(MouseButtonRequest req, CancellationToken ct)
    {
        return _mouse.SetButtonAsync(req.Button ?? string.Empty, req.Down, req.ItfSel, ct);
    }
}

