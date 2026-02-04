using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sets mouse buttons mask on the controlled device.
/// </summary>
public sealed class MouseButtonsMaskUseCase
{
    private readonly IMouseControl _mouse;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public MouseButtonsMaskUseCase(IMouseControl mouse)
    {
        _mouse = mouse;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<MouseButtonsMaskResult> ExecuteAsync(MouseButtonsMaskRequest req, CancellationToken ct)
    {
        return _mouse.SetButtonsMaskAsync(req.ButtonsMask, req.ItfSel, ct);
    }
}

