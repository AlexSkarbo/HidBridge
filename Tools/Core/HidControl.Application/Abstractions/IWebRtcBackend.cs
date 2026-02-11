namespace HidControl.Application.Abstractions;

/// <summary>
/// Backend abstraction for WebRTC control-plane features. This hides server-specific concerns like
/// helper supervision and in-memory signaling state behind a stable interface.
/// </summary>
public interface IWebRtcBackend
{
    /// <summary>
    /// Gets the current peer counts per room (signaling connections).
    /// </summary>
    /// <returns>Snapshot mapping room id -&gt; peer count.</returns>
    IReadOnlyDictionary<string, int> GetRoomPeerCountsSnapshot();

    /// <summary>
    /// Gets rooms that currently have a helper peer process.
    /// </summary>
    /// <returns>Set of room ids that have helpers.</returns>
    IReadOnlySet<string> GetHelperRoomsSnapshot();

    /// <summary>
    /// Ensures a helper peer exists for a room.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="qualityPreset">Optional video quality preset (used for video rooms only).</param>
    /// <param name="bitrateKbps">Optional target bitrate (kbps) used for video rooms only.</param>
    /// <param name="fps">Optional target FPS used for video rooms only.</param>
    /// <param name="imageQuality">Optional image quality level (1-100) used for video rooms only.</param>
    /// <param name="captureInput">Optional capture input string used for video rooms only.</param>
    /// <param name="encoder">Optional encoder selection used for video rooms only.</param>
    /// <param name="codec">Optional codec selection used for video rooms only.</param>
    /// <returns>Start result.</returns>
    (bool ok, bool started, int? pid, string? error) EnsureHelperStarted(
        string room,
        string? qualityPreset = null,
        int? bitrateKbps = null,
        int? fps = null,
        int? imageQuality = null,
        string? captureInput = null,
        string? encoder = null,
        string? codec = null);

    /// <summary>
    /// Stops a helper peer for a room.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <returns>Stop result.</returns>
    (bool ok, bool stopped, string? error) StopHelper(string room);

    /// <summary>
    /// Returns a stable hardware identity string when available (used for room id generation).
    /// </summary>
    /// <returns>Device id in hex or null if unknown.</returns>
    string? GetDeviceIdHex();

    /// <summary>
    /// Default STUN URL for WebRTC control peer.
    /// </summary>
    string StunUrl { get; }

    /// <summary>
    /// TURN URLs (for TURN REST) used by browsers.
    /// </summary>
    IReadOnlyList<string> TurnUrls { get; }

    /// <summary>
    /// TURN REST shared secret (coturn static auth secret). Empty when TURN REST is disabled.
    /// </summary>
    string TurnSharedSecret { get; }

    /// <summary>
    /// TURN REST credential TTL in seconds.
    /// </summary>
    int TurnTtlSeconds { get; }

    /// <summary>
    /// TURN REST username suffix ("expires:username").
    /// </summary>
    string TurnUsername { get; }

    /// <summary>
    /// WebRTC UI/client join timeout in milliseconds.
    /// </summary>
    int ClientJoinTimeoutMs { get; }

    /// <summary>
    /// WebRTC UI/client connect timeout in milliseconds.
    /// </summary>
    int ClientConnectTimeoutMs { get; }

    /// <summary>
    /// WebRTC rooms cleanup interval in seconds.
    /// </summary>
    int RoomsCleanupIntervalSeconds { get; }

    /// <summary>
    /// WebRTC room idle stop threshold in seconds.
    /// </summary>
    int RoomIdleStopSeconds { get; }

    /// <summary>
    /// Maximum concurrent helper processes.
    /// </summary>
    int RoomsMaxHelpers { get; }

    /// <summary>
    /// Enables room registry persistence to disk.
    /// </summary>
    bool RoomsPersistenceEnabled { get; }

    /// <summary>
    /// Path to the room registry file.
    /// </summary>
    string RoomsPersistencePath { get; }

    /// <summary>
    /// TTL in seconds for persisted room entries before cleanup.
    /// </summary>
    int RoomsPersistenceTtlSeconds { get; }
}
