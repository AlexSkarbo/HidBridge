using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for restarting a WebRTC video room helper.
/// </summary>
public sealed class RestartWebRtcVideoRoomUseCase
{
    private readonly IWebRtcRoomsService _rooms;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="rooms">Rooms service.</param>
    public RestartWebRtcVideoRoomUseCase(IWebRtcRoomsService rooms)
    {
        _rooms = rooms;
    }

    /// <summary>
    /// Executes the use-case.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="qualityPreset">Optional quality preset.</param>
    /// <param name="bitrateKbps">Optional bitrate kbps.</param>
    /// <param name="fps">Optional fps.</param>
    /// <param name="imageQuality">Optional image quality 1..100.</param>
    /// <param name="captureInput">Optional capture input.</param>
    /// <param name="encoder">Optional encoder mode.</param>
    /// <param name="codec">Optional codec mode.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Restart result.</returns>
    public Task<WebRtcRoomRestartResult> Execute(string room, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec, CancellationToken ct)
    {
        return _rooms.RestartVideoAsync(room, qualityPreset, bitrateKbps, fps, imageQuality, captureInput, encoder, codec, ct);
    }
}
