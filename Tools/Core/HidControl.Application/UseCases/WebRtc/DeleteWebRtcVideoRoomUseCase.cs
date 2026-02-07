using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for deleting a WebRTC video room helper.
/// </summary>
public sealed class DeleteWebRtcVideoRoomUseCase
{
    private readonly IWebRtcRoomsService _rooms;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="rooms">Rooms service.</param>
    public DeleteWebRtcVideoRoomUseCase(IWebRtcRoomsService rooms)
    {
        _rooms = rooms;
    }

    /// <summary>
    /// Executes the use-case.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Delete result.</returns>
    public Task<WebRtcRoomDeleteResult> Execute(string room, CancellationToken ct)
    {
        if (string.Equals(room, "video", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new WebRtcRoomDeleteResult(false, room, false, "cannot_delete_video"));
        }
        return _rooms.DeleteAsync(room, ct);
    }
}

