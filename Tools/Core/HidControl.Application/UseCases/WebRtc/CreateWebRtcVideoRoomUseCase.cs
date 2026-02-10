using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for creating a WebRTC video room (and ensuring a helper peer is started for it).
/// </summary>
public sealed class CreateWebRtcVideoRoomUseCase
{
    private readonly IWebRtcRoomsService _rooms;
    private readonly IWebRtcRoomIdService _roomIds;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="rooms">Rooms service.</param>
    /// <param name="roomIds">Room id generator.</param>
    public CreateWebRtcVideoRoomUseCase(IWebRtcRoomsService rooms, IWebRtcRoomIdService roomIds)
    {
        _rooms = rooms;
        _roomIds = roomIds;
    }

    /// <summary>
    /// Executes the use-case.
    /// </summary>
    /// <param name="room">Optional room id.</param>
    /// <param name="qualityPreset">Optional video quality preset.</param>
    /// <param name="bitrateKbps">Optional target bitrate (kbps).</param>
    /// <param name="fps">Optional target FPS.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Create result.</returns>
    public Task<WebRtcRoomCreateResult> Execute(string? room, string? qualityPreset, int? bitrateKbps, int? fps, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(qualityPreset))
        {
            string q = qualityPreset.Trim().ToLowerInvariant();
            if (q != "low" && q != "balanced" && q != "high")
            {
                return Task.FromResult(new WebRtcRoomCreateResult(false, room, false, null, "bad_quality_preset"));
            }
        }
        if (bitrateKbps is int b && (b < 200 || b > 12000))
        {
            return Task.FromResult(new WebRtcRoomCreateResult(false, room, false, null, "bad_bitrate_kbps"));
        }
        if (fps is int f && (f < 5 || f > 60))
        {
            return Task.FromResult(new WebRtcRoomCreateResult(false, room, false, null, "bad_fps"));
        }

        string? r = string.IsNullOrWhiteSpace(room) ? _roomIds.GenerateVideoRoomId() : room;
        return _rooms.CreateVideoAsync(r, qualityPreset, bitrateKbps, fps, ct);
    }
}
