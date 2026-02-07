namespace HidControl.Application.Abstractions;

/// <summary>
/// Provides WebRTC room discovery and lifecycle operations used by thin API endpoints and clients.
/// </summary>
public interface IWebRtcRoomsService
{
    /// <summary>
    /// Lists known rooms with their current peer counts and helper state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List result.</returns>
    Task<WebRtcRoomsSnapshot> ListAsync(CancellationToken ct);

    /// <summary>
    /// Creates a room (or normalizes a provided room id) and ensures a helper peer is started for it.
    /// </summary>
    /// <param name="room">Optional room id. If null/empty, a room id is generated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Create result.</returns>
    Task<WebRtcRoomCreateResult> CreateAsync(string? room, CancellationToken ct);

    /// <summary>
    /// Stops a room helper and removes the room (where applicable).
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Delete result.</returns>
    Task<WebRtcRoomDeleteResult> DeleteAsync(string room, CancellationToken ct);
}

/// <summary>
/// Generates WebRTC room ids that are stable-ish and hardware-associated when possible.
/// </summary>
public interface IWebRtcRoomIdService
{
    /// <summary>
    /// Generates a room id for a control-plane (DataChannel) session.
    /// </summary>
    /// <returns>Room id.</returns>
    string GenerateControlRoomId();

    /// <summary>
    /// Generates a room id for a video-plane (WebRTC media) session.
    /// </summary>
    /// <returns>Room id.</returns>
    string GenerateVideoRoomId();
}

/// <summary>
/// Provides ICE server configuration for WebRTC clients (STUN/TURN).
/// </summary>
public interface IWebRtcIceService
{
    /// <summary>
    /// Returns ICE servers that should be used by clients.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ICE config.</returns>
    Task<WebRtcIceConfig> GetIceConfigAsync(CancellationToken ct);
}

/// <summary>
/// Provides WebRTC client configuration (timeouts and limits) that the UI can consume.
/// </summary>
public interface IWebRtcConfigService
{
    /// <summary>
    /// Returns WebRTC client/UI configuration values.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Config.</returns>
    Task<WebRtcClientConfig> GetClientConfigAsync(CancellationToken ct);
}

/// <summary>
/// Snapshot of WebRTC rooms for UI display.
/// </summary>
/// <param name="Rooms">Rooms.</param>
public sealed record WebRtcRoomsSnapshot(IReadOnlyList<WebRtcRoomInfo> Rooms);

/// <summary>
/// Room info for UI display.
/// </summary>
/// <param name="Room">Room id.</param>
/// <param name="Peers">Peer count currently connected to signaling room.</param>
/// <param name="HasHelper">True if a helper process is known for the room.</param>
/// <param name="IsControl">True if room is the default control room.</param>
public sealed record WebRtcRoomInfo(string Room, int Peers, bool HasHelper, bool IsControl);

/// <summary>
/// Result of creating a room.
/// </summary>
/// <param name="Ok">Ok.</param>
/// <param name="Room">Room id.</param>
/// <param name="Started">True if a helper was started by this call.</param>
/// <param name="Pid">Helper pid if started/known.</param>
/// <param name="Error">Error code if failed.</param>
public sealed record WebRtcRoomCreateResult(bool Ok, string? Room, bool Started, int? Pid, string? Error);

/// <summary>
/// Result of deleting a room.
/// </summary>
/// <param name="Ok">Ok.</param>
/// <param name="Room">Room id.</param>
/// <param name="Stopped">True if a helper was stopped by this call.</param>
/// <param name="Error">Error code if failed.</param>
public sealed record WebRtcRoomDeleteResult(bool Ok, string Room, bool Stopped, string? Error);

/// <summary>
/// WebRTC ICE servers payload (STUN/TURN).
/// </summary>
/// <param name="TtlSeconds">TTL for ephemeral credentials (if TURN REST is enabled).</param>
/// <param name="IceServers">ICE server objects (as DTOs) suitable for browser RTCPeerConnection config.</param>
public sealed record WebRtcIceConfig(int? TtlSeconds, IReadOnlyList<WebRtcIceServer> IceServers);

/// <summary>
/// ICE server DTO compatible with browser RTCPeerConnection config.
/// </summary>
/// <param name="Urls">STUN/TURN URLs.</param>
/// <param name="Username">Optional TURN username.</param>
/// <param name="Credential">Optional TURN credential (password or token).</param>
/// <param name="CredentialType">Optional credential type ("password" is common for TURN REST).</param>
public sealed record WebRtcIceServer(IReadOnlyList<string> Urls, string? Username, string? Credential, string? CredentialType);

/// <summary>
/// WebRTC UI/client configuration values.
/// </summary>
/// <param name="JoinTimeoutMs">Join timeout in milliseconds.</param>
/// <param name="ConnectTimeoutMs">Connect timeout in milliseconds.</param>
/// <param name="RoomsCleanupIntervalSeconds">Room cleanup interval in seconds.</param>
/// <param name="RoomIdleStopSeconds">Idle stop threshold in seconds.</param>
/// <param name="RoomsMaxHelpers">Maximum concurrent helper processes.</param>
public sealed record WebRtcClientConfig(
    int JoinTimeoutMs,
    int ConnectTimeoutMs,
    int RoomsCleanupIntervalSeconds,
    int RoomIdleStopSeconds,
    int RoomsMaxHelpers);
