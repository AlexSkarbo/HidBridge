using HidBridge.Contracts;

namespace HidBridge.SessionOrchestrator;

/// <summary>
/// Represents a deterministic control-arbitration conflict raised by the session orchestrator.
/// </summary>
public sealed class ControlArbitrationException : InvalidOperationException
{
    /// <summary>
    /// Initializes one control-arbitration exception.
    /// </summary>
    public ControlArbitrationException(
        string code,
        string message,
        string sessionId,
        SessionControlLeaseBody? activeLease = null,
        string? requestedParticipantId = null,
        string? actedBy = null)
        : base(message)
    {
        Code = code;
        SessionId = sessionId;
        CurrentControllerParticipantId = activeLease?.ParticipantId;
        CurrentControllerPrincipalId = activeLease?.PrincipalId;
        LeaseExpiresAtUtc = activeLease?.ExpiresAtUtc;
        RequestedParticipantId = requestedParticipantId;
        ActedBy = actedBy;
    }

    /// <summary>
    /// Gets the stable machine-readable arbitration code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the target session identifier.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the currently active controller participant identifier, when one exists.
    /// </summary>
    public string? CurrentControllerParticipantId { get; }

    /// <summary>
    /// Gets the currently active controller principal identifier, when one exists.
    /// </summary>
    public string? CurrentControllerPrincipalId { get; }

    /// <summary>
    /// Gets the lease expiration timestamp for the active controller, when one exists.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAtUtc { get; }

    /// <summary>
    /// Gets the participant identifier requested by the caller, when provided.
    /// </summary>
    public string? RequestedParticipantId { get; }

    /// <summary>
    /// Gets the acting principal identifier, when provided.
    /// </summary>
    public string? ActedBy { get; }

    /// <summary>
    /// Creates a conflict when a different controller currently holds an active lease.
    /// </summary>
    public static ControlArbitrationException LeaseHeldByOther(
        string sessionId,
        SessionControlLeaseBody activeLease,
        string requestedParticipantId,
        string actedBy)
        => new(
            code: "E_CONTROL_LEASE_HELD_BY_OTHER",
            message: $"Session {sessionId} is currently controlled by {activeLease.PrincipalId}.",
            sessionId: sessionId,
            activeLease: activeLease,
            requestedParticipantId: requestedParticipantId,
            actedBy: actedBy);

    /// <summary>
    /// Creates a conflict when a release request points to a participant that is not the current lease holder.
    /// </summary>
    public static ControlArbitrationException ParticipantMismatch(
        string sessionId,
        SessionControlLeaseBody activeLease,
        string requestedParticipantId,
        string actedBy)
        => new(
            code: "E_CONTROL_PARTICIPANT_MISMATCH",
            message: $"Current control lease is held by participant {activeLease.ParticipantId}, not {requestedParticipantId}.",
            sessionId: sessionId,
            activeLease: activeLease,
            requestedParticipantId: requestedParticipantId,
            actedBy: actedBy);

    /// <summary>
    /// Creates a conflict when a participant role is not eligible for control.
    /// </summary>
    public static ControlArbitrationException ParticipantNotEligible(
        string sessionId,
        SessionParticipantBody participant)
        => new(
            code: "E_CONTROL_NOT_ELIGIBLE",
            message: $"Participant {participant.ParticipantId} with role {participant.Role} is not eligible for active control.",
            sessionId: sessionId,
            requestedParticipantId: participant.ParticipantId);
}
