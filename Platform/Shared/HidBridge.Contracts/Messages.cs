namespace HidBridge.Contracts;

/// <summary>
/// Wraps a contract payload with routing, identity, and correlation metadata.
/// </summary>
public sealed record AgentEnvelope(
    string V,
    string Kind,
    string MessageId,
    string TraceId,
    DateTimeOffset Ts,
    string TenantId,
    string EndpointId,
    string AgentId,
    object Body,
    string? CorrelationId = null);

/// <summary>
/// Describes one advertised agent capability together with its semantic version and optional metadata.
/// </summary>
public sealed record CapabilityDescriptor(string Name, string Version, IReadOnlyDictionary<string, string>? Meta = null);

/// <summary>
/// Identifies the connector runtime model behind an agent.
/// </summary>
public enum ConnectorType
{
    Installed,
    Agentless,
    HidBridge,
    MediaGateway,
}

/// <summary>
/// Represents the current operational status of an agent.
/// </summary>
public enum AgentStatus
{
    Online,
    Degraded,
    Offline,
    Retired,
}

/// <summary>
/// Selects the end-user experience profile for a session.
/// </summary>
public enum SessionProfile
{
    UltraLowLatency,
    Balanced,
    QualityFirst,
}

/// <summary>
/// Defines the role granted to a participant inside a session.
/// </summary>
public enum SessionRole
{
    Owner,
    Controller,
    Observer,
    Presenter,
}

/// <summary>
/// Represents the lifecycle state of a control or collaboration session.
/// </summary>
public enum SessionState
{
    Requested,
    Preparing,
    Arming,
    Active,
    Recovering,
    Terminating,
    Ended,
    Failed,
}

/// <summary>
/// Represents the lifecycle state of one session share grant.
/// </summary>
public enum SessionShareStatus
{
    Requested,
    Pending,
    Accepted,
    Rejected,
    Revoked,
    Expired,
}

/// <summary>
/// Indicates which transport path is expected to carry the command payload.
/// </summary>
public enum CommandChannel
{
    Hid,
    DataChannel,
    WebSocket,
    AgentRpc,
}

/// <summary>
/// Represents one normalized WebRTC signaling message type.
/// </summary>
public enum WebRtcSignalKind
{
    Offer,
    Answer,
    IceCandidate,
    Bye,
    Heartbeat,
    Command,
    Ack,
}

/// <summary>
/// Represents the delivery or execution outcome of a command.
/// </summary>
public enum CommandStatus
{
    Accepted,
    Applied,
    Rejected,
    Timeout,
}

/// <summary>
/// Defines normalized transport message categories used by relay adapters.
/// </summary>
public enum TransportMessageKind
{
    Command,
    Ack,
    Event,
    Heartbeat,
}

/// <summary>
/// Groups errors by subsystem so they can be routed and analyzed consistently.
/// </summary>
public enum ErrorDomain
{
    Auth,
    Agent,
    Session,
    Command,
    Media,
    Hid,
    Uart,
    Transport,
    System,
}

/// <summary>
/// Describes a normalized contract error that can be propagated across services and agents.
/// </summary>
public sealed record ErrorInfo(
    ErrorDomain Domain,
    string Code,
    string Message,
    bool Retryable,
    IReadOnlyDictionary<string, object?>? Details = null);

/// <summary>
/// Payload emitted when an agent registers itself with the control plane.
/// </summary>
public sealed record AgentRegisterBody(
    ConnectorType ConnectorType,
    string AgentVersion,
    IReadOnlyList<CapabilityDescriptor> Capabilities,
    IReadOnlyDictionary<string, object?>? Health = null);

/// <summary>
/// Payload emitted periodically to report agent liveness and load.
/// </summary>
public sealed record AgentHeartbeatBody(
    AgentStatus Status,
    long? UptimeMs = null,
    IReadOnlyDictionary<string, object?>? Load = null);

/// <summary>
/// Requests a new session bound to a specific target agent and endpoint.
/// </summary>
public sealed record SessionOpenBody(
    string SessionId,
    SessionProfile Profile,
    string RequestedBy,
    string TargetAgentId,
    string? TargetEndpointId = null,
    SessionRole ShareMode = SessionRole.Owner,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null,
    string? TransportProvider = null);

/// <summary>
/// Requests an orderly session shutdown.
/// </summary>
public sealed record SessionCloseBody(string SessionId, string Reason);

/// <summary>
/// Describes one participant currently attached to a collaborative session.
/// </summary>
public sealed record SessionParticipantBody(
    string ParticipantId,
    string PrincipalId,
    SessionRole Role,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Describes one share grant that may later become an active participant.
/// </summary>
public sealed record SessionShareBody(
    string ShareId,
    string PrincipalId,
    string GrantedBy,
    SessionRole Role,
    SessionShareStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Requests an explicit participant upsert for a collaboration session.
/// </summary>
public sealed record SessionParticipantUpsertBody(
    string ParticipantId,
    string PrincipalId,
    SessionRole Role,
    string AddedBy,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Requests removal of one participant from a collaboration session.
/// </summary>
public sealed record SessionParticipantRemoveBody(
    string ParticipantId,
    string RemovedBy,
    string Reason,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Requests creation of a new share grant for a collaboration session.
/// </summary>
public sealed record SessionShareGrantBody(
    string ShareId,
    string PrincipalId,
    string GrantedBy,
    SessionRole Role,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Requests a collaboration invitation or access request that must be approved before the final invite is issued.
/// </summary>
public sealed record SessionInvitationRequestBody(
    string ShareId,
    string PrincipalId,
    string RequestedBy,
    SessionRole RequestedRole,
    string? Message = null,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Requests an approval or decline decision for one pending invitation request.
/// </summary>
public sealed record SessionInvitationDecisionBody(
    string ShareId,
    string ActedBy,
    SessionRole? GrantedRole = null,
    string? Reason = null,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Requests a share status transition such as accept, reject, revoke, or expire.
/// </summary>
public sealed record SessionShareTransitionBody(
    string ShareId,
    string ActedBy,
    string? Reason = null,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Represents the currently active controller lease for a session.
/// </summary>
public sealed record SessionControlLeaseBody(
    string ParticipantId,
    string PrincipalId,
    string GrantedBy,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Requests control over a session for the specified participant.
/// </summary>
public sealed record SessionControlRequestBody(
    string ParticipantId,
    string RequestedBy,
    int? LeaseSeconds = null,
    string? Reason = null,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Requests server-side ensure flow for control lease:
/// resolve/reuse/create session when needed, then request lease.
/// </summary>
public sealed record SessionControlEnsureBody(
    string ParticipantId,
    string RequestedBy,
    string? EndpointId = null,
    SessionProfile Profile = SessionProfile.UltraLowLatency,
    int? LeaseSeconds = null,
    string? Reason = null,
    bool AutoCreateSessionIfMissing = true,
    bool PreferLiveRelaySession = true,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Reports one control-ensure result with effective session and lease snapshot.
/// </summary>
public sealed record SessionControlEnsureResultBody(
    string RequestedSessionId,
    string EffectiveSessionId,
    SessionControlLeaseBody Lease,
    bool SessionCreated,
    bool SessionReused,
    DateTimeOffset ResolvedAtUtc);

/// <summary>
/// Grants or force-transfers control to the specified participant.
/// </summary>
public sealed record SessionControlGrantBody(
    string ParticipantId,
    string GrantedBy,
    int? LeaseSeconds = null,
    string? Reason = null,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Releases the currently active control lease for a session.
/// </summary>
public sealed record SessionControlReleaseBody(
    string ActedBy,
    string? ParticipantId = null,
    string? Reason = null,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Describes one executable command routed through a session.
/// </summary>
public sealed record CommandRequestBody(
    string CommandId,
    string SessionId,
    CommandChannel Channel,
    string Action,
    IReadOnlyDictionary<string, object?> Args,
    int TimeoutMs,
    string IdempotencyKey,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Reports the result of a command execution attempt.
/// </summary>
public sealed record CommandAckBody(
    string CommandId,
    CommandStatus Status,
    ErrorInfo? Error = null,
    IReadOnlyDictionary<string, double>? Metrics = null);

/// <summary>
/// Represents one command entry inside batch dispatch request.
/// </summary>
public sealed record SessionCommandBatchItemBody(
    string CommandId,
    CommandChannel Channel,
    string Action,
    IReadOnlyDictionary<string, object?> Args,
    int TimeoutMs,
    string IdempotencyKey);

/// <summary>
/// Represents one batch command dispatch request scoped to one session route.
/// </summary>
public sealed record SessionCommandBatchDispatchBody(
    IReadOnlyList<SessionCommandBatchItemBody> Commands,
    string? TenantId = null,
    string? OrganizationId = null,
    IReadOnlyList<string>? OperatorRoles = null);

/// <summary>
/// Represents one command result entry returned from batch dispatch endpoint.
/// </summary>
public sealed record SessionCommandBatchResultBody(
    string CommandId,
    CommandStatus Status,
    ErrorInfo? Error = null,
    IReadOnlyDictionary<string, double>? Metrics = null);

/// <summary>
/// Represents aggregate result of one batch dispatch attempt.
/// </summary>
public sealed record SessionCommandBatchAckBody(
    IReadOnlyList<SessionCommandBatchResultBody> Results,
    int AppliedCount,
    int RejectedCount,
    int TimeoutCount);

/// <summary>
/// Carries normalized HID command arguments for edge transport execution.
/// </summary>
public sealed record TransportHidCommandArgsBody(
    string? Text = null,
    string? Shortcut = null,
    int? Usage = null,
    int? Modifiers = null,
    int? Dx = null,
    int? Dy = null,
    int? Wheel = null,
    int? Delta = null,
    string? Button = null,
    bool? Down = null,
    int? HoldMs = null,
    int? InterfaceSelector = null);

/// <summary>
/// Carries one typed transport command message.
/// </summary>
public sealed record TransportCommandMessageBody(
    TransportMessageKind Kind,
    string CommandId,
    string SessionId,
    string Action,
    TransportHidCommandArgsBody Args,
    int TimeoutMs,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Carries one typed transport acknowledgment message.
/// </summary>
public sealed record TransportAckMessageBody(
    TransportMessageKind Kind,
    string CommandId,
    CommandStatus Status,
    DateTimeOffset AcknowledgedAtUtc,
    ErrorInfo? Error = null,
    IReadOnlyDictionary<string, double>? Metrics = null);

/// <summary>
/// Carries one typed transport heartbeat message.
/// </summary>
public sealed record TransportHeartbeatMessageBody(
    TransportMessageKind Kind,
    string SessionId,
    string PeerId,
    string EndpointId,
    string PrincipalId,
    DateTimeOffset Utc);

/// <summary>
/// Carries one typed transport event message.
/// </summary>
public sealed record TransportEventMessageBody(
    TransportMessageKind Kind,
    string SessionId,
    string PeerId,
    string Name,
    DateTimeOffset OccurredAtUtc,
    string? ReasonCode = null,
    string? Message = null);

/// <summary>
/// Represents one persisted command journal entry.
/// </summary>
public sealed record CommandJournalEntryBody(
    string CommandId,
    string SessionId,
    string AgentId,
    CommandChannel Channel,
    string Action,
    IReadOnlyDictionary<string, object?> Args,
    int TimeoutMs,
    string IdempotencyKey,
    CommandStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc = null,
    ErrorInfo? Error = null,
    IReadOnlyDictionary<string, double>? Metrics = null,
    string? ParticipantId = null,
    string? PrincipalId = null,
    string? ShareId = null);

/// <summary>
/// Carries one WebRTC signaling message persisted for one session.
/// </summary>
public sealed record WebRtcSignalMessageBody(
    string SessionId,
    int Sequence,
    WebRtcSignalKind Kind,
    string SenderPeerId,
    string? RecipientPeerId,
    string Payload,
    string? Mid = null,
    int? MLineIndex = null,
    DateTimeOffset? CreatedAtUtc = null);

/// <summary>
/// Represents one WebRTC peer presence snapshot inside a session.
/// </summary>
public sealed record WebRtcPeerStateBody(
    string SessionId,
    string PeerId,
    string? EndpointId,
    bool IsOnline,
    DateTimeOffset LastSeenAtUtc,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Carries one WebRTC signaling publish request.
/// </summary>
public sealed record WebRtcSignalPublishBody(
    WebRtcSignalKind Kind,
    string SenderPeerId,
    string? RecipientPeerId,
    string Payload,
    string? Mid = null,
    int? MLineIndex = null);

/// <summary>
/// Carries one WebRTC peer presence publish request.
/// </summary>
public sealed record WebRtcPeerPresenceBody(
    string? EndpointId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Carries one WebRTC relay command ACK publish request.
/// </summary>
public sealed record WebRtcCommandAckPublishBody(
    CommandStatus? Status = null,
    ErrorInfo? Error = null,
    IReadOnlyDictionary<string, double>? Metrics = null);

/// <summary>
/// Carries one command envelope published through the WebRTC relay path.
/// </summary>
public sealed record WebRtcCommandEnvelopeBody(
    string SessionId,
    int Sequence,
    string? EndpointId,
    string? RecipientPeerId,
    CommandRequestBody Command,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Carries one transport health report for a session route.
/// </summary>
public sealed record SessionTransportHealthBody(
    string SessionId,
    string AgentId,
    string EndpointId,
    string Provider,
    string ProviderSource,
    bool Connected,
    string Status,
    IReadOnlyDictionary<string, object?> Metrics,
    CommandJournalEntryBody? LastCommandAck = null,
    DateTimeOffset? ReportedAtUtc = null,
    int? OnlinePeerCount = null,
    DateTimeOffset? LastPeerSeenAtUtc = null,
    string? LastPeerState = null,
    string? LastPeerFailureReason = null,
    int? LastPeerConsecutiveFailures = null,
    int? LastPeerReconnectBackoffMs = null,
    DateTimeOffset? LastRelayAckAtUtc = null,
    bool? MediaReady = null,
    string? MediaState = null,
    string? MediaFailureReason = null,
    DateTimeOffset? MediaReportedAtUtc = null,
    string? MediaStreamId = null,
    string? MediaSource = null,
    string? MediaPlaybackUrl = null,
    string? MediaStreamKind = null,
    MediaVideoDescriptorBody? MediaVideo = null,
    MediaAudioDescriptorBody? MediaAudio = null);

/// <summary>
/// Carries one transport-readiness projection for WebRTC relay command routing.
/// </summary>
public sealed record SessionTransportReadinessBody(
    string SessionId,
    string AgentId,
    string EndpointId,
    string Provider,
    string ProviderSource,
    bool Ready,
    string ReasonCode,
    string Reason,
    bool Connected,
    int OnlinePeerCount,
    string? LastPeerState = null,
    string? LastPeerFailureReason = null,
    DateTimeOffset? LastPeerSeenAtUtc = null,
    DateTimeOffset? LastRelayAckAtUtc = null,
    bool? MediaReady = null,
    string? MediaState = null,
    string? MediaFailureReason = null,
    DateTimeOffset? MediaReportedAtUtc = null,
    string? MediaStreamId = null,
    string? MediaSource = null,
    string? MediaPlaybackUrl = null,
    string? MediaStreamKind = null,
    MediaVideoDescriptorBody? MediaVideo = null,
    MediaAudioDescriptorBody? MediaAudio = null,
    IReadOnlyDictionary<string, object?>? Metrics = null,
    DateTimeOffset? EvaluatedAtUtc = null);

/// <summary>
/// Describes typed video stream properties published by edge runtimes.
/// </summary>
public sealed record MediaVideoDescriptorBody(
    string? Codec = null,
    int? Width = null,
    int? Height = null,
    double? FrameRate = null,
    int? BitrateKbps = null);

/// <summary>
/// Describes typed audio stream properties published by edge runtimes.
/// </summary>
public sealed record MediaAudioDescriptorBody(
    string? Codec = null,
    int? Channels = null,
    int? SampleRateHz = null,
    int? BitrateKbps = null);

/// <summary>
/// Carries one media stream registration payload published by an edge agent.
/// </summary>
public sealed record SessionMediaStreamRegistrationBody(
    string PeerId,
    string EndpointId,
    string StreamId,
    bool Ready,
    string State,
    DateTimeOffset ReportedAtUtc,
    string? FailureReason = null,
    string? Source = null,
    string? PlaybackUrl = null,
    string? StreamKind = null,
    MediaVideoDescriptorBody? Video = null,
    MediaAudioDescriptorBody? Audio = null,
    IReadOnlyDictionary<string, object?>? Metrics = null);

/// <summary>
/// Represents one persisted media stream snapshot tracked by the platform media layer.
/// </summary>
public sealed record SessionMediaStreamSnapshotBody(
    string SessionId,
    string PeerId,
    string EndpointId,
    string StreamId,
    bool Ready,
    string State,
    DateTimeOffset ReportedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? FailureReason = null,
    string? Source = null,
    string? PlaybackUrl = null,
    string? StreamKind = null,
    MediaVideoDescriptorBody? Video = null,
    MediaAudioDescriptorBody? Audio = null,
    IReadOnlyDictionary<string, object?>? Metrics = null);

/// <summary>
/// Represents one normalized timeline item built from audit, telemetry, and command journal records.
/// </summary>
public sealed record TimelineEntryBody(
    string Kind,
    string Title,
    DateTimeOffset OccurredAtUtc,
    string? SessionId = null,
    IReadOnlyDictionary<string, object?>? Data = null);

/// <summary>
/// Carries numeric telemetry values emitted by an agent or platform component.
/// </summary>
public sealed record TelemetryEventBody(
    string Scope,
    IReadOnlyDictionary<string, object?> Metrics,
    string? SessionId = null,
    DateTimeOffset? CreatedAtUtc = null);

/// <summary>
/// Carries human-readable audit information for inventory, session, and command activity.
/// </summary>
public sealed record AuditEventBody(
    string Category,
    string Message,
    string? SessionId = null,
    IReadOnlyDictionary<string, object?>? Data = null,
    DateTimeOffset? CreatedAtUtc = null);
