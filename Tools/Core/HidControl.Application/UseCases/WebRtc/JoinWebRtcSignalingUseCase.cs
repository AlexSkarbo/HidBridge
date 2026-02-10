using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for joining a WebRTC signaling room.
/// </summary>
public sealed class JoinWebRtcSignalingUseCase
{
    private readonly IWebRtcSignalingService _signaling;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="signaling">Signaling service.</param>
    public JoinWebRtcSignalingUseCase(IWebRtcSignalingService signaling)
    {
        _signaling = signaling;
    }

    /// <summary>
    /// Executes room join for a client, leaving previous room membership when present.
    /// </summary>
    /// <param name="currentRoom">Current room id, if any.</param>
    /// <param name="requestedRoom">Requested room id.</param>
    /// <param name="clientId">Client id.</param>
    /// <returns>Join result.</returns>
    public JoinWebRtcSignalingResult Execute(string? currentRoom, string? requestedRoom, string clientId)
    {
        WebRtcRoomNormalizationResult normalized = _signaling.NormalizeRoom(requestedRoom);
        if (!normalized.Ok || string.IsNullOrWhiteSpace(normalized.Room))
        {
            return new JoinWebRtcSignalingResult(false, normalized.Error ?? "bad_room", null, null, 0);
        }

        if (!string.IsNullOrWhiteSpace(currentRoom))
        {
            _signaling.Leave(currentRoom, clientId);
        }

        string room = normalized.Room;
        WebRtcRoomKind kind = _signaling.GetRoomKind(room);
        WebRtcJoinResult join = _signaling.TryJoin(room, clientId);
        if (!join.Ok)
        {
            return new JoinWebRtcSignalingResult(false, join.Error ?? "join_failed", room, kind, join.Peers);
        }

        return new JoinWebRtcSignalingResult(true, null, room, kind, join.Peers);
    }
}

/// <summary>
/// Result of joining signaling room.
/// </summary>
/// <param name="Ok">True when join succeeded.</param>
/// <param name="Error">Error code when failed.</param>
/// <param name="Room">Room id when available.</param>
/// <param name="Kind">Room kind when available.</param>
/// <param name="Peers">Peer count in room after operation (or current count on failure).</param>
public sealed record JoinWebRtcSignalingResult(
    bool Ok,
    string? Error,
    string? Room,
    WebRtcRoomKind? Kind,
    int Peers);
