using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for listing WebRTC rooms.
/// </summary>
public sealed class ListWebRtcRoomsUseCase
{
    private readonly IWebRtcRoomsService _svc;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="svc">Service.</param>
    public ListWebRtcRoomsUseCase(IWebRtcRoomsService svc)
    {
        _svc = svc;
    }

    /// <summary>
    /// Executes the use-case.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Snapshot.</returns>
    public Task<WebRtcRoomsSnapshot> Execute(CancellationToken ct) => _svc.ListAsync(ct);
}

