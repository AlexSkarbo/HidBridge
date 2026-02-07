using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for deleting a WebRTC room (stopping its helper peer).
/// </summary>
public sealed class DeleteWebRtcRoomUseCase
{
    private readonly IWebRtcRoomsService _svc;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="svc">Service.</param>
    public DeleteWebRtcRoomUseCase(IWebRtcRoomsService svc)
    {
        _svc = svc;
    }

    /// <summary>
    /// Executes the use-case.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Delete result.</returns>
    public Task<WebRtcRoomDeleteResult> Execute(string room, CancellationToken ct) => _svc.DeleteAsync(room, ct);
}

