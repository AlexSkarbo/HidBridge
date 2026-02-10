using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for leaving a WebRTC signaling room.
/// </summary>
public sealed class LeaveWebRtcSignalingUseCase
{
    private readonly IWebRtcSignalingService _signaling;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="signaling">Signaling service.</param>
    public LeaveWebRtcSignalingUseCase(IWebRtcSignalingService signaling)
    {
        _signaling = signaling;
    }

    /// <summary>
    /// Executes room leave for the client and returns room state after leave.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="clientId">Client id.</param>
    /// <returns>Leave result.</returns>
    public LeaveWebRtcSignalingResult Execute(string room, string clientId)
    {
        _signaling.Leave(room, clientId);
        int peersLeft = _signaling.GetRoomPeerCountsSnapshot().TryGetValue(room, out int c) ? c : 0;
        WebRtcRoomKind kind = _signaling.GetRoomKind(room);
        return new LeaveWebRtcSignalingResult(room, kind, peersLeft);
    }
}

/// <summary>
/// Result of leaving signaling room.
/// </summary>
/// <param name="Room">Room id.</param>
/// <param name="Kind">Room kind.</param>
/// <param name="PeersLeft">Peer count after leaving.</param>
public sealed record LeaveWebRtcSignalingResult(string Room, WebRtcRoomKind Kind, int PeersLeft);
