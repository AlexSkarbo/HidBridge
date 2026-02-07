namespace HidControl.Contracts;

/// <summary>
/// WebRTC ICE server configuration response.
/// </summary>
/// <param name="Ok">Ok.</param>
/// <param name="TtlSeconds">TTL for TURN REST credentials (seconds) or null when TURN REST is disabled.</param>
/// <param name="IceServers">ICE servers.</param>
public sealed record WebRtcIceResponse(bool Ok, int? TtlSeconds, IReadOnlyList<WebRtcIceServerDto> IceServers);

/// <summary>
/// ICE server DTO compatible with browser RTCPeerConnection config.
/// </summary>
/// <param name="Urls">STUN/TURN URLs.</param>
/// <param name="Username">Optional TURN username.</param>
/// <param name="Credential">Optional TURN credential.</param>
/// <param name="CredentialType">Optional credential type (commonly "password").</param>
public sealed record WebRtcIceServerDto(IReadOnlyList<string> Urls, string? Username, string? Credential, string? CredentialType);

/// <summary>
/// WebRTC UI/client configuration response.
/// </summary>
/// <param name="Ok">Ok.</param>
/// <param name="JoinTimeoutMs">Join timeout in milliseconds.</param>
/// <param name="ConnectTimeoutMs">Connect timeout in milliseconds.</param>
/// <param name="RoomsCleanupIntervalSeconds">Room cleanup interval in seconds.</param>
/// <param name="RoomIdleStopSeconds">Room idle stop threshold in seconds.</param>
/// <param name="RoomsMaxHelpers">Maximum concurrent helper processes.</param>
public sealed record WebRtcConfigResponse(
    bool Ok,
    int JoinTimeoutMs,
    int ConnectTimeoutMs,
    int RoomsCleanupIntervalSeconds,
    int RoomIdleStopSeconds,
    int RoomsMaxHelpers);

/// <summary>
/// WebRTC rooms list response.
/// </summary>
/// <param name="Ok">Ok.</param>
/// <param name="Rooms">Rooms.</param>
public sealed record WebRtcRoomsResponse(bool Ok, IReadOnlyList<WebRtcRoomDto> Rooms);

/// <summary>
/// WebRTC room DTO.
/// </summary>
/// <param name="Room">Room id.</param>
/// <param name="Peers">Peer count.</param>
/// <param name="HasHelper">True if helper peer process exists for this room.</param>
/// <param name="IsControl">True if this is the default control room.</param>
public sealed record WebRtcRoomDto(string Room, int Peers, bool HasHelper, bool IsControl);

/// <summary>
/// Create room request body.
/// </summary>
/// <param name="Room">Optional room id.</param>
public sealed record WebRtcCreateRoomRequest(string? Room);

/// <summary>
/// Create room response.
/// </summary>
/// <param name="Ok">Ok.</param>
/// <param name="Room">Room id.</param>
/// <param name="Started">True if helper was started by this call.</param>
/// <param name="Pid">Helper pid if known.</param>
/// <param name="Error">Error code if failed.</param>
public sealed record WebRtcCreateRoomResponse(bool Ok, string? Room, bool Started, int? Pid, string? Error);

/// <summary>
/// Delete room response.
/// </summary>
/// <param name="Ok">Ok.</param>
/// <param name="Room">Room id.</param>
/// <param name="Stopped">True if helper was stopped.</param>
/// <param name="Error">Error code if failed.</param>
public sealed record WebRtcDeleteRoomResponse(bool Ok, string Room, bool Stopped, string? Error);

