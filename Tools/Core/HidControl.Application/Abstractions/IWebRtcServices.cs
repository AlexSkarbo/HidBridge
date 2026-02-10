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
/// Provides in-memory WebRTC signaling room state operations (join/leave/peers).
/// This service is transport-agnostic and does not depend on WebSocket types.
/// </summary>
public interface IWebRtcSignalingService
{
    /// <summary>
    /// Returns a point-in-time snapshot of room peer counts.
    /// </summary>
    /// <returns>Room id to peer count mapping.</returns>
    IReadOnlyDictionary<string, int> GetRoomPeerCountsSnapshot();

    /// <summary>
    /// Returns the room kind inferred from room id naming convention.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <returns>Room kind.</returns>
    WebRtcRoomKind GetRoomKind(string room);

    /// <summary>
    /// Validates and normalizes a room id.
    /// </summary>
    /// <param name="room">Room id candidate.</param>
    /// <returns>Normalization result.</returns>
    WebRtcRoomNormalizationResult NormalizeRoom(string? room);

    /// <summary>
    /// Attempts to join a client into a room.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="clientId">Client id.</param>
    /// <returns>Join result.</returns>
    WebRtcJoinResult TryJoin(string room, string clientId);

    /// <summary>
    /// Returns peer ids currently present in the room.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="excludeClientId">Optional client id to exclude.</param>
    /// <returns>Peer ids snapshot.</returns>
    IReadOnlyList<string> GetPeerIds(string room, string? excludeClientId = null);

    /// <summary>
    /// Checks whether a client is currently a member of a room.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="clientId">Client id.</param>
    /// <returns>True when the client is joined.</returns>
    bool IsJoined(string room, string clientId);

    /// <summary>
    /// Removes a client from a room if present.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="clientId">Client id.</param>
    void Leave(string room, string clientId);
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

/// <summary>
/// Result of room id normalization/validation.
/// </summary>
/// <param name="Ok">True when room id is valid.</param>
/// <param name="Room">Normalized room id when valid.</param>
/// <param name="Error">Error code when invalid.</param>
public sealed record WebRtcRoomNormalizationResult(bool Ok, string? Room, string? Error);

/// <summary>
/// Result of joining a signaling room.
/// </summary>
/// <param name="Ok">True when join succeeded.</param>
/// <param name="Peers">Peer count in room after operation (or current count on failure).</param>
/// <param name="Error">Error code when failed.</param>
public sealed record WebRtcJoinResult(bool Ok, int Peers, string? Error);

/// <summary>
/// Logical WebRTC room kind.
/// </summary>
public enum WebRtcRoomKind
{
    /// <summary>Control-plane room.</summary>
    Control = 0,

    /// <summary>Video-plane room.</summary>
    Video = 1
}
