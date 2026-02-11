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
    /// <param name="imageQuality">Optional image quality level (1-100).</param>
    /// <param name="captureInput">Optional capture input string for helper.</param>
    /// <param name="encoder">Optional encoder selection for helper.</param>
    /// <param name="codec">Optional codec selection for helper.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Create result.</returns>
    public Task<WebRtcRoomCreateResult> Execute(string? room, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(qualityPreset))
        {
            string q = qualityPreset.Trim().ToLowerInvariant();
            if (q != "low" && q != "low-latency" && q != "balanced" && q != "high" && q != "optimal")
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
        if (imageQuality is int iq && (iq < 1 || iq > 100))
        {
            return Task.FromResult(new WebRtcRoomCreateResult(false, room, false, null, "bad_image_quality"));
        }
        if (!string.IsNullOrWhiteSpace(encoder))
        {
            string e = encoder.Trim().ToLowerInvariant();
            if (e != "auto" &&
                e != "cpu" &&
                e != "hw" &&
                e != "nvenc" &&
                e != "amf" &&
                e != "qsv" &&
                e != "v4l2m2m" &&
                e != "vaapi")
            {
                return Task.FromResult(new WebRtcRoomCreateResult(false, room, false, null, "bad_encoder"));
            }
        }
        if (!string.IsNullOrWhiteSpace(codec))
        {
            string c = codec.Trim().ToLowerInvariant();
            if (c != "auto" && c != "vp8" && c != "h264")
            {
                return Task.FromResult(new WebRtcRoomCreateResult(false, room, false, null, "bad_codec"));
            }
        }

        string? r = string.IsNullOrWhiteSpace(room) ? _roomIds.GenerateVideoRoomId() : room;
        return _rooms.CreateVideoAsync(r, qualityPreset, bitrateKbps, fps, imageQuality, captureInput, encoder, codec, ct);
    }
}
