using HidControl.Contracts;

namespace HidControl.ClientSdk;

/// <summary>
/// WebRTC helper APIs for <see cref="HidControlClient"/>.
/// </summary>
public static class WebRtcClientExtensions
{
    /// <summary>
    /// Gets ICE servers (STUN/TURN) for browser peers.
    /// </summary>
    /// <param name="client">Client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ICE response.</returns>
    public static Task<WebRtcIceResponse?> GetWebRtcIceAsync(this HidControlClient client, CancellationToken ct = default)
        => client.GetAsync<WebRtcIceResponse>("/status/webrtc/ice", ct);

    /// <summary>
    /// Gets UI/client config values (timeouts and limits).
    /// </summary>
    /// <param name="client">Client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Config response.</returns>
    public static Task<WebRtcConfigResponse?> GetWebRtcConfigAsync(this HidControlClient client, CancellationToken ct = default)
        => client.GetAsync<WebRtcConfigResponse>("/status/webrtc/config", ct);

    /// <summary>
    /// Lists rooms known to the signaling server and helper supervisor.
    /// </summary>
    /// <param name="client">Client.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rooms response.</returns>
    public static Task<WebRtcRoomsResponse?> ListWebRtcRoomsAsync(this HidControlClient client, CancellationToken ct = default)
        => client.GetAsync<WebRtcRoomsResponse>("/status/webrtc/rooms", ct);

    /// <summary>
    /// Creates a room and ensures a helper peer is started for it.
    /// </summary>
    /// <param name="client">Client.</param>
    /// <param name="room">Optional room id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Create response.</returns>
    public static Task<WebRtcCreateRoomResponse?> CreateWebRtcRoomAsync(this HidControlClient client, string? room = null, CancellationToken ct = default)
        => client.PostAsync<WebRtcCreateRoomRequest, WebRtcCreateRoomResponse>("/status/webrtc/rooms", new WebRtcCreateRoomRequest(room), ct);

    /// <summary>
    /// Deletes a room (stops its helper peer).
    /// </summary>
    /// <param name="client">Client.</param>
    /// <param name="room">Room id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Delete response.</returns>
    public static Task<WebRtcDeleteRoomResponse?> DeleteWebRtcRoomAsync(this HidControlClient client, string room, CancellationToken ct = default)
        => client.DeleteAsync<WebRtcDeleteRoomResponse>("/status/webrtc/rooms/" + Uri.EscapeDataString(room), ct);
}

