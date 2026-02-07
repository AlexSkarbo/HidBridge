using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for creating a WebRTC room (and ensuring a helper peer is started for it).
/// </summary>
public sealed class CreateWebRtcRoomUseCase
{
    private readonly IWebRtcRoomsService _svc;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="svc">Service.</param>
    public CreateWebRtcRoomUseCase(IWebRtcRoomsService svc)
    {
        _svc = svc;
    }

    /// <summary>
    /// Executes the use-case.
    /// </summary>
    /// <param name="room">Optional room id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Create result.</returns>
    public Task<WebRtcRoomCreateResult> Execute(string? room, CancellationToken ct) => _svc.CreateAsync(room, ct);
}

