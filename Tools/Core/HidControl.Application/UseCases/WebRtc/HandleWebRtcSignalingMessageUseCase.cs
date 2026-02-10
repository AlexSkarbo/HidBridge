using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Orchestrates handling of a single WebRTC signaling message type for a connected client.
/// </summary>
public sealed class HandleWebRtcSignalingMessageUseCase
{
    private readonly JoinWebRtcSignalingUseCase _joinUseCase;
    private readonly ValidateWebRtcSignalUseCase _validateSignalUseCase;
    private readonly LeaveWebRtcSignalingUseCase _leaveUseCase;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="joinUseCase">Join operation use-case.</param>
    /// <param name="validateSignalUseCase">Signal validation use-case.</param>
    /// <param name="leaveUseCase">Leave operation use-case.</param>
    public HandleWebRtcSignalingMessageUseCase(
        JoinWebRtcSignalingUseCase joinUseCase,
        ValidateWebRtcSignalUseCase validateSignalUseCase,
        LeaveWebRtcSignalingUseCase leaveUseCase)
    {
        _joinUseCase = joinUseCase;
        _validateSignalUseCase = validateSignalUseCase;
        _leaveUseCase = leaveUseCase;
    }

    /// <summary>
    /// Executes routing/validation for one signaling command.
    /// </summary>
    /// <param name="currentRoom">Current room id of the client, if any.</param>
    /// <param name="requestedRoom">Requested room id from message, if any.</param>
    /// <param name="clientId">Client id.</param>
    /// <param name="messageType">Inbound message type.</param>
    /// <returns>Handling result that the transport layer can serialize/broadcast.</returns>
    public HandleWebRtcSignalingResult Execute(
        string? currentRoom,
        string? requestedRoom,
        string clientId,
        string messageType)
    {
        if (string.Equals(messageType, "join", StringComparison.OrdinalIgnoreCase))
        {
            JoinWebRtcSignalingResult join = _joinUseCase.Execute(currentRoom, requestedRoom, clientId);
            if (!join.Ok || string.IsNullOrWhiteSpace(join.Room) || join.Kind is null)
            {
                return new HandleWebRtcSignalingResult(
                    Action: WebRtcSignalingAction.Join,
                    Ok: false,
                    NextRoom: null,
                    Error: join.Error ?? "join_failed",
                    Room: join.Room,
                    Kind: join.Kind,
                    Peers: join.Peers,
                    PeersLeft: 0,
                    HasPeerLeftBroadcast: false);
            }

            return new HandleWebRtcSignalingResult(
                Action: WebRtcSignalingAction.Join,
                Ok: true,
                NextRoom: join.Room,
                Error: null,
                Room: join.Room,
                Kind: join.Kind,
                Peers: join.Peers,
                PeersLeft: 0,
                HasPeerLeftBroadcast: false);
        }

        if (string.Equals(messageType, "signal", StringComparison.OrdinalIgnoreCase))
        {
            ValidateWebRtcSignalResult canSignal = _validateSignalUseCase.Execute(currentRoom, clientId);
            if (!canSignal.Ok || string.IsNullOrWhiteSpace(currentRoom))
            {
                return new HandleWebRtcSignalingResult(
                    Action: WebRtcSignalingAction.Signal,
                    Ok: false,
                    NextRoom: currentRoom,
                    Error: canSignal.Error ?? "not_joined",
                    Room: currentRoom,
                    Kind: null,
                    Peers: 0,
                    PeersLeft: 0,
                    HasPeerLeftBroadcast: false);
            }

            return new HandleWebRtcSignalingResult(
                Action: WebRtcSignalingAction.Signal,
                Ok: true,
                NextRoom: currentRoom,
                Error: null,
                Room: currentRoom,
                Kind: null,
                Peers: 0,
                PeersLeft: 0,
                HasPeerLeftBroadcast: false);
        }

        if (string.Equals(messageType, "leave", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(currentRoom))
            {
                return new HandleWebRtcSignalingResult(
                    Action: WebRtcSignalingAction.Leave,
                    Ok: true,
                    NextRoom: null,
                    Error: null,
                    Room: null,
                    Kind: null,
                    Peers: 0,
                    PeersLeft: 0,
                    HasPeerLeftBroadcast: false);
            }

            LeaveWebRtcSignalingResult leave = _leaveUseCase.Execute(currentRoom, clientId);
            return new HandleWebRtcSignalingResult(
                Action: WebRtcSignalingAction.Leave,
                Ok: true,
                NextRoom: null,
                Error: null,
                Room: leave.Room,
                Kind: leave.Kind,
                Peers: 0,
                PeersLeft: leave.PeersLeft,
                HasPeerLeftBroadcast: true);
        }

        if (string.Equals(messageType, "ping", StringComparison.OrdinalIgnoreCase))
        {
            return new HandleWebRtcSignalingResult(
                Action: WebRtcSignalingAction.Ping,
                Ok: true,
                NextRoom: currentRoom,
                Error: null,
                Room: currentRoom,
                Kind: null,
                Peers: 0,
                PeersLeft: 0,
                HasPeerLeftBroadcast: false);
        }

        return new HandleWebRtcSignalingResult(
            Action: WebRtcSignalingAction.Unsupported,
            Ok: false,
            NextRoom: currentRoom,
            Error: "unsupported_type",
            Room: currentRoom,
            Kind: null,
            Peers: 0,
            PeersLeft: 0,
            HasPeerLeftBroadcast: false);
    }
}

/// <summary>
/// Result produced by <see cref="HandleWebRtcSignalingMessageUseCase"/>.
/// </summary>
/// <param name="Action">Resolved signaling action.</param>
/// <param name="Ok">True when operation is allowed/succeeded.</param>
/// <param name="NextRoom">Room id that should become current after this operation.</param>
/// <param name="Error">Error code when <paramref name="Ok"/> is false.</param>
/// <param name="Room">Room associated with the operation (if any).</param>
/// <param name="Kind">Room kind when known.</param>
/// <param name="Peers">Peers count for join results.</param>
/// <param name="PeersLeft">Peers count after leave.</param>
/// <param name="HasPeerLeftBroadcast">Whether a <c>peer_left</c> broadcast should be emitted.</param>
public sealed record HandleWebRtcSignalingResult(
    WebRtcSignalingAction Action,
    bool Ok,
    string? NextRoom,
    string? Error,
    string? Room,
    WebRtcRoomKind? Kind,
    int Peers,
    int PeersLeft,
    bool HasPeerLeftBroadcast);

/// <summary>
/// Supported signaling actions resolved from incoming message type.
/// </summary>
public enum WebRtcSignalingAction
{
    /// <summary>Join room operation.</summary>
    Join = 0,

    /// <summary>Forward signaling payload operation.</summary>
    Signal = 1,

    /// <summary>Leave room operation.</summary>
    Leave = 2,

    /// <summary>Heartbeat ping operation.</summary>
    Ping = 3,

    /// <summary>Unsupported operation.</summary>
    Unsupported = 4
}
