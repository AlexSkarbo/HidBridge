using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Represents the collaboration dashboard for one session.
/// </summary>
public sealed record SessionDashboardReadModel(
    string SessionId,
    string AgentId,
    string EndpointId,
    SessionProfile Profile,
    SessionRole OwnerRole,
    SessionState State,
    string RequestedBy,
    int ParticipantCount,
    int ActiveControllers,
    int Observers,
    int Presenters,
    int RequestedInvitations,
    int PendingShares,
    int AcceptedShares,
    int RejectedShares,
    int RevokedShares,
    int CommandCount,
    int FailedCommandCount,
    int TimelineEntries,
    string? ActiveControllerParticipantId,
    string? ActiveControllerPrincipalId,
    string? ControlGrantedBy,
    DateTimeOffset? ControlLeaseExpiresAtUtc,
    DateTimeOffset? LastCommandAtUtc,
    DateTimeOffset? LastActivityAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc);

/// <summary>
/// Represents one participant activity projection inside a session.
/// </summary>
public sealed record ParticipantActivityReadModel(
    string ParticipantId,
    string PrincipalId,
    SessionRole Role,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int CommandCount,
    int FailedCommandCount,
    DateTimeOffset? LastCommandAtUtc,
    bool DerivedFromShare,
    string? ShareId,
    SessionShareStatus? ShareStatus,
    bool IsCurrentController);

/// <summary>
/// Represents the invitation and share dashboard for one session.
/// </summary>
public sealed record ShareDashboardReadModel(
    string SessionId,
    int RequestedCount,
    int PendingCount,
    int AcceptedCount,
    int RejectedCount,
    int RevokedCount,
    IReadOnlyList<SessionShareSnapshot> Requested,
    IReadOnlyList<SessionShareSnapshot> Pending,
    IReadOnlyList<SessionShareSnapshot> Accepted,
    IReadOnlyList<SessionShareSnapshot> Rejected,
    IReadOnlyList<SessionShareSnapshot> Revoked);

/// <summary>
/// Represents the operator-focused timeline projection for one session.
/// </summary>
public sealed record OperatorTimelineReadModel(
    string SessionId,
    int TotalEntries,
    IReadOnlyList<TimelineEntryBody> Entries);

/// <summary>
/// Represents one control-arbitration dashboard projection for a session.
/// </summary>
public sealed record ControlDashboardReadModel(
    string SessionId,
    SessionControlLeaseBody? ActiveLease,
    IReadOnlyList<ControlCandidateReadModel> Candidates);

/// <summary>
/// Represents one candidate that may hold the active control lease.
/// </summary>
public sealed record ControlCandidateReadModel(
    string ParticipantId,
    string PrincipalId,
    SessionRole Role,
    bool IsEligible,
    bool IsCurrentController,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset UpdatedAtUtc);
