namespace HidBridge.ControlPlane.Web.Models;

/// <summary>
/// Represents the basic ControlPlane health payload.
/// </summary>
public sealed record ApiHealthViewModel(
    string Service,
    string Status,
    DateTimeOffset Utc);

/// <summary>
/// Represents the runtime UART and auth configuration exposed by the ControlPlane API.
/// </summary>
public sealed record ApiRuntimeViewModel(
    string TransportProvider,
    string Port,
    int BaudRate,
    int MouseSelector,
    int KeyboardSelector,
    string AgentId,
    string EndpointId,
    string PersistenceProvider,
    string DataRoot,
    string SqlSchema,
    bool SqlApplyMigrations,
    ApiRuntimePolicyBootstrapViewModel PolicyBootstrap,
    ApiRuntimeAuthenticationViewModel Authentication,
    ApiRuntimePolicyRevisionLifecycleViewModel PolicyRevisionLifecycle,
    ApiRuntimeMaintenanceViewModel Maintenance);

/// <summary>
/// Represents policy bootstrap settings projected by the API runtime endpoint.
/// </summary>
public sealed record ApiRuntimePolicyBootstrapViewModel(
    string ScopeId,
    string? TenantId,
    string? OrganizationId,
    bool ViewerRoleRequired,
    bool ModeratorOverrideEnabled,
    bool AdminOverrideEnabled,
    int AssignmentCount);

/// <summary>
/// Represents runtime API authentication settings.
/// </summary>
public sealed record ApiRuntimeAuthenticationViewModel(
    bool Enabled,
    string? Authority,
    string? Audience,
    bool RequireHttpsMetadata,
    bool AllowHeaderFallback,
    IReadOnlyList<string> BearerOnlyPrefixes,
    IReadOnlyList<string> CallerContextRequiredPrefixes,
    IReadOnlyList<string> HeaderFallbackDisabledPatterns);

/// <summary>
/// Represents policy revision lifecycle settings.
/// </summary>
public sealed record ApiRuntimePolicyRevisionLifecycleViewModel(
    double MaintenanceIntervalSeconds,
    double RetentionDays,
    int MaxRevisionsPerEntity);

/// <summary>
/// Represents session maintenance settings.
/// </summary>
public sealed record ApiRuntimeMaintenanceViewModel(
    double LeaseSeconds,
    double RecoveryGraceSeconds,
    double CleanupIntervalSeconds,
    double AuditRetentionDays,
    double TelemetryRetentionDays);

/// <summary>
/// Represents the fleet inventory dashboard consumed by the operator shell.
/// </summary>
public sealed record InventoryDashboardViewModel(
    DateTimeOffset GeneratedAtUtc,
    int TotalAgents,
    int TotalEndpoints,
    int TotalSessions,
    int ActiveSessions,
    int RecoveringSessions,
    int FailedSessions,
    int ActiveControlLeases,
    IReadOnlyList<ConnectorTypeSummaryViewModel> ConnectorTypes,
    IReadOnlyList<InventoryEndpointCardViewModel> Endpoints);

/// <summary>
/// Represents one connector-type summary tile on the fleet dashboard.
/// </summary>
public sealed record ConnectorTypeSummaryViewModel(
    string ConnectorType,
    int Count);

/// <summary>
/// Represents one endpoint card on the fleet dashboard.
/// </summary>
public sealed record InventoryEndpointCardViewModel(
    string EndpointId,
    string? AgentId,
    string? ConnectorType,
    string Status,
    int CapabilityCount,
    IReadOnlyList<string> CapabilityNames,
    string? ActiveSessionId,
    string? ActiveSessionState,
    string? ActiveControllerPrincipalId,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Represents one paged query result returned by projection endpoints.
/// </summary>
/// <typeparam name="TItem">The row type.</typeparam>
public sealed record ProjectionPageViewModel<TItem>(
    int Total,
    int Skip,
    int Take,
    IReadOnlyList<TItem> Items);

/// <summary>
/// Represents one flattened session row in the operator shell.
/// </summary>
public sealed record SessionProjectionItemViewModel(
    string SessionId,
    string AgentId,
    string EndpointId,
    string Profile,
    string Role,
    string State,
    string RequestedBy,
    int ParticipantCount,
    int ShareCount,
    int AcceptedShareCount,
    string? ActiveControllerPrincipalId,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc);

/// <summary>
/// Represents one session open request issued by the operator shell.
/// </summary>
public sealed class SessionOpenRequestViewModel
{
    /// <summary>
    /// Gets or sets the session identifier.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the requested session profile.
    /// </summary>
    public string Profile { get; set; } = "Balanced";

    /// <summary>
    /// Gets or sets the principal that requested the session.
    /// </summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target agent identifier.
    /// </summary>
    public string TargetAgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional target endpoint identifier.
    /// </summary>
    public string? TargetEndpointId { get; set; }

    /// <summary>
    /// Gets or sets the room owner role.
    /// </summary>
    public string ShareMode { get; set; } = "Owner";

    /// <summary>
    /// Gets or sets the tenant identifier.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the organization identifier.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Gets or sets the operator roles carried with the request.
    /// </summary>
    public IReadOnlyList<string> OperatorRoles { get; set; } = [];
}

/// <summary>
/// Represents one session command dispatch request issued from the session room.
/// </summary>
public sealed class SessionCommandDispatchRequestViewModel
{
    /// <summary>
    /// Gets or sets the command identifier.
    /// </summary>
    public string CommandId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the transport channel expected by the backend.
    /// </summary>
    public string Channel { get; set; } = "Hid";

    /// <summary>
    /// Gets or sets the normalized command action name.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets command arguments.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Args { get; set; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the command timeout.
    /// </summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Gets or sets the idempotency key.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tenant identifier.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the organization identifier.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Gets or sets the operator roles carried with the request.
    /// </summary>
    public IReadOnlyList<string> OperatorRoles { get; set; } = [];
}

/// <summary>
/// Represents one command dispatch acknowledgement.
/// </summary>
public sealed record CommandAckViewModel(
    string CommandId,
    string Status,
    ErrorInfoViewModel? Error = null,
    IReadOnlyDictionary<string, double>? Metrics = null);

/// <summary>
/// Represents one persisted command journal entry projected from transport health diagnostics.
/// </summary>
public sealed record CommandJournalEntryViewModel(
    string CommandId,
    string SessionId,
    string AgentId,
    string Channel,
    string Action,
    IReadOnlyDictionary<string, object?> Args,
    int TimeoutMs,
    string IdempotencyKey,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc = null,
    ErrorInfoViewModel? Error = null,
    IReadOnlyDictionary<string, double>? Metrics = null,
    string? ParticipantId = null,
    string? PrincipalId = null,
    string? ShareId = null);

/// <summary>
/// Represents one transport route health snapshot for a session.
/// </summary>
public sealed record SessionTransportHealthViewModel(
    string SessionId,
    string AgentId,
    string EndpointId,
    string Provider,
    string ProviderSource,
    bool Connected,
    string Status,
    IReadOnlyDictionary<string, object?> Metrics,
    CommandJournalEntryViewModel? LastCommandAck = null,
    DateTimeOffset? ReportedAtUtc = null);

/// <summary>
/// Represents one WebRTC signaling message.
/// </summary>
public sealed record WebRtcSignalMessageViewModel(
    string SessionId,
    int Sequence,
    string Kind,
    string SenderPeerId,
    string? RecipientPeerId,
    string Payload,
    string? Mid = null,
    int? MLineIndex = null,
    DateTimeOffset? CreatedAtUtc = null);

/// <summary>
/// Represents one signaling publish request emitted by the operator shell.
/// </summary>
public sealed class WebRtcSignalPublishRequestViewModel
{
    /// <summary>
    /// Gets or sets signal kind (Offer/Answer/IceCandidate/Bye/Heartbeat).
    /// </summary>
    public string Kind { get; set; } = "Heartbeat";

    /// <summary>
    /// Gets or sets sender peer identifier.
    /// </summary>
    public string SenderPeerId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional target peer identifier.
    /// </summary>
    public string? RecipientPeerId { get; set; }

    /// <summary>
    /// Gets or sets payload serialized by the signaling plane.
    /// </summary>
    public string Payload { get; set; } = "{}";

    /// <summary>
    /// Gets or sets optional SDP media id.
    /// </summary>
    public string? Mid { get; set; }

    /// <summary>
    /// Gets or sets optional SDP media line index.
    /// </summary>
    public int? MLineIndex { get; set; }
}

/// <summary>
/// Represents one bulk session-close result returned by cleanup actions.
/// </summary>
public sealed record SessionBulkCloseResultViewModel(
    string Action,
    bool DryRun,
    string Reason,
    DateTimeOffset GeneratedAtUtc,
    int ScannedSessions,
    int MatchedSessions,
    int ClosedSessions,
    int SkippedSessions,
    int FailedClosures,
    int? StaleAfterMinutes,
    IReadOnlyList<string> MatchedSessionIds,
    IReadOnlyList<string> ClosedSessionIds,
    IReadOnlyList<string> SkippedSessionIds,
    IReadOnlyList<string> Errors);

/// <summary>
/// Represents one backend error payload.
/// </summary>
public sealed record ErrorInfoViewModel(
    string Domain,
    string Code,
    string Message,
    bool Retryable);

/// <summary>
/// Represents one share grant request issued from the session room.
/// </summary>
public sealed class SessionShareGrantRequestViewModel
{
    /// <summary>
    /// Gets or sets the share identifier.
    /// </summary>
    public string ShareId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the invited principal.
    /// </summary>
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the granting principal.
    /// </summary>
    public string GrantedBy { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the granted role.
    /// </summary>
    public string Role { get; set; } = "Observer";

    /// <summary>
    /// Gets or sets the tenant identifier.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the organization identifier.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Gets or sets the operator roles carried with the request.
    /// </summary>
    public IReadOnlyList<string> OperatorRoles { get; set; } = [];
}

/// <summary>
/// Represents one invitation request issued from the session room.
/// </summary>
public sealed class SessionInvitationRequestViewModel
{
    /// <summary>
    /// Gets or sets the share identifier.
    /// </summary>
    public string ShareId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the requesting principal.
    /// </summary>
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the operator principal that initiated the request.
    /// </summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the requested role.
    /// </summary>
    public string RequestedRole { get; set; } = "Observer";

    /// <summary>
    /// Gets or sets the optional invitation note.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the tenant identifier.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the organization identifier.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Gets or sets the operator roles carried with the request.
    /// </summary>
    public IReadOnlyList<string> OperatorRoles { get; set; } = [];
}

/// <summary>
/// Represents one audit dashboard response in the operator shell.
/// </summary>
public sealed record AuditDashboardViewModel(
    DateTimeOffset GeneratedAtUtc,
    int TotalEvents,
    int SessionScopedEvents,
    int GlobalEvents,
    DateTimeOffset? LatestEventAtUtc,
    IReadOnlyList<AuditCategorySummaryViewModel> Categories,
    IReadOnlyList<SessionAuditSummaryViewModel> Sessions,
    IReadOnlyList<AuditRecentEntryViewModel> RecentEntries);

/// <summary>
/// Represents one audit category bucket.
/// </summary>
public sealed record AuditCategorySummaryViewModel(
    string Category,
    int Count);

/// <summary>
/// Represents one session bucket inside the audit dashboard.
/// </summary>
public sealed record SessionAuditSummaryViewModel(
    string SessionId,
    int Count,
    DateTimeOffset? LatestEventAtUtc);

/// <summary>
/// Represents one recent audit event row.
/// </summary>
public sealed record AuditRecentEntryViewModel(
    string Category,
    string Message,
    string? SessionId,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Represents one telemetry dashboard response in the operator shell.
/// </summary>
public sealed record TelemetryDashboardViewModel(
    DateTimeOffset GeneratedAtUtc,
    int TotalEvents,
    int SessionScopedEvents,
    DateTimeOffset? LatestEventAtUtc,
    IReadOnlyList<TelemetryScopeSummaryViewModel> Scopes,
    IReadOnlyList<TelemetrySignalViewModel> Signals,
    IReadOnlyList<TelemetrySessionHealthViewModel> Sessions);

/// <summary>
/// Represents one telemetry scope bucket.
/// </summary>
public sealed record TelemetryScopeSummaryViewModel(
    string Scope,
    int Count,
    DateTimeOffset? LatestEventAtUtc);

/// <summary>
/// Represents one telemetry signal card.
/// </summary>
public sealed record TelemetrySignalViewModel(
    string Scope,
    string MetricName,
    string LatestValue,
    double? LatestNumericValue,
    DateTimeOffset LatestAtUtc);

/// <summary>
/// Represents one session telemetry summary row.
/// </summary>
public sealed record TelemetrySessionHealthViewModel(
    string SessionId,
    int EventCount,
    DateTimeOffset? LatestEventAtUtc,
    IReadOnlyDictionary<string, string> LatestMetrics);

/// <summary>
/// Represents one collaboration dashboard response for a session.
/// </summary>
public sealed record SessionDashboardViewModel(
    string SessionId,
    string AgentId,
    string EndpointId,
    string Profile,
    string OwnerRole,
    string State,
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
    DateTimeOffset? LeaseExpiresAtUtc,
    string? StateReason,
    DateTimeOffset? StateChangedAtUtc);

/// <summary>
/// Represents one control dashboard response for a session.
/// </summary>
public sealed record ControlDashboardViewModel(
    string SessionId,
    SessionControlLeaseViewModel? ActiveLease,
    IReadOnlyList<ControlCandidateViewModel> Candidates);

/// <summary>
/// Represents the current active control lease.
/// </summary>
public sealed record SessionControlLeaseViewModel(
    string ParticipantId,
    string PrincipalId,
    string GrantedBy,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Represents one candidate for active control.
/// </summary>
public sealed record ControlCandidateViewModel(
    string ParticipantId,
    string PrincipalId,
    string Role,
    bool IsEligible,
    bool IsCurrentController,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Represents one invitation or share row shown in the operator lobby.
/// </summary>
public sealed record SessionShareViewModel(
    string ShareId,
    string PrincipalId,
    string GrantedBy,
    string Role,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Represents the invitation-oriented lobby view for one session.
/// </summary>
public sealed record SessionLobbyViewModel(
    string SessionId,
    IReadOnlyList<SessionShareViewModel> Requested,
    IReadOnlyList<SessionShareViewModel> Pending,
    IReadOnlyList<SessionShareViewModel> Accepted,
    IReadOnlyList<SessionShareViewModel> Rejected,
    IReadOnlyList<SessionShareViewModel> Revoked);

/// <summary>
/// Represents one participant activity row rendered in the session room.
/// </summary>
public sealed record ParticipantActivityViewModel(
    string ParticipantId,
    string PrincipalId,
    string Role,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int CommandCount,
    int FailedCommandCount,
    DateTimeOffset? LastCommandAtUtc,
    bool DerivedFromShare,
    string? ShareId,
    string? ShareStatus,
    bool IsCurrentController);

/// <summary>
/// Represents one operator-focused session timeline.
/// </summary>
public sealed record OperatorTimelineViewModel(
    string SessionId,
    int TotalEntries,
    IReadOnlyList<TimelineEntryViewModel> Entries);

/// <summary>
/// Represents one timeline entry shown in the session room.
/// </summary>
public sealed record TimelineEntryViewModel(
    string Kind,
    string Title,
    DateTimeOffset OccurredAtUtc,
    string? SessionId);

/// <summary>
/// Represents one structured authorization denial returned by the backend.
/// </summary>
public sealed record AuthorizationDeniedViewModel(
    int Status,
    string Code,
    string Message,
    string? SessionId,
    string? ShareId,
    string? ParticipantId,
    IReadOnlyList<string>? RequiredRoles,
    string? RequiredTenantId,
    string? RequiredOrganizationId,
    AuthorizationDeniedCallerViewModel Caller);

/// <summary>
/// Represents the caller metadata projected in one authorization denial response.
/// </summary>
public sealed record AuthorizationDeniedCallerViewModel(
    string? SubjectId,
    string? PrincipalId,
    string? TenantId,
    string? OrganizationId,
    IReadOnlyList<string> Roles);

/// <summary>
/// Represents one structured control-arbitration conflict returned by the backend.
/// </summary>
public sealed record ControlArbitrationConflictViewModel(
    string SessionId,
    string Code,
    string Error,
    string? RequestedParticipantId,
    string? ActedBy,
    ControlArbitrationCurrentControllerViewModel? CurrentController);

/// <summary>
/// Represents one active-controller snapshot projected in a control-arbitration conflict.
/// </summary>
public sealed record ControlArbitrationCurrentControllerViewModel(
    string? ParticipantId,
    string? PrincipalId,
    DateTimeOffset? ExpiresAtUtc);


/// <summary>
/// Represents the policy governance summary shown in the operator shell.
/// </summary>
public sealed record PolicyDiagnosticsSummaryViewModel(
    DateTimeOffset GeneratedAtUtc,
    int TotalRevisions,
    int VisibleScopeCount,
    int VisibleAssignmentCount,
    int PruneCandidateCount,
    DateTimeOffset? LatestRevisionAtUtc,
    int RetentionDays,
    int MaxRevisionsPerEntity,
    IReadOnlyList<PolicyRevisionEntityTypeSummaryViewModel> EntityTypes,
    IReadOnlyList<PolicyRevisionScopeSummaryViewModel> Scopes);

/// <summary>
/// Represents one policy revision entity-type bucket.
/// </summary>
public sealed record PolicyRevisionEntityTypeSummaryViewModel(
    string EntityType,
    int Count,
    DateTimeOffset? LatestRevisionAtUtc);

/// <summary>
/// Represents one policy scope bucket in the governance summary.
/// </summary>
public sealed record PolicyRevisionScopeSummaryViewModel(
    string ScopeId,
    string? TenantId,
    string? OrganizationId,
    int RevisionCount,
    int AssignmentCount,
    DateTimeOffset? LatestRevisionAtUtc);

/// <summary>
/// Represents one persisted policy revision row.
/// </summary>
public sealed record PolicyRevisionSnapshotViewModel(
    string RevisionId,
    string EntityType,
    string EntityId,
    string ScopeId,
    string? PrincipalId,
    int Version,
    string SnapshotJson,
    DateTimeOffset CreatedAtUtc,
    string? Source);

/// <summary>
/// Represents the result of a manual policy prune operation.
/// </summary>
public sealed record PolicyRevisionPruneResultViewModel(
    DateTimeOffset ExecutedAtUtc,
    int DeletedRevisionCount,
    int RetentionDays,
    int MaxRevisionsPerEntity);

/// <summary>
/// Represents one editable policy scope row in the operator shell.
/// </summary>
public sealed record PolicyScopeViewModel(
    string ScopeId,
    string? TenantId,
    string? OrganizationId,
    bool ViewerRoleRequired,
    bool ModeratorOverrideEnabled,
    bool AdminOverrideEnabled,
    DateTimeOffset UpdatedAtUtc,
    bool IsActive);

/// <summary>
/// Represents one editable policy assignment row in the operator shell.
/// </summary>
public sealed record PolicyAssignmentViewModel(
    string AssignmentId,
    string ScopeId,
    string PrincipalId,
    IReadOnlyList<string> Roles,
    DateTimeOffset UpdatedAtUtc,
    string? Source,
    bool IsActive);

/// <summary>
/// Represents one policy scope upsert request sent by the operator shell.
/// </summary>
public sealed class PolicyScopeUpsertRequestViewModel
{
    /// <summary>
    /// Creates an empty scope upsert request.
    /// </summary>
    public PolicyScopeUpsertRequestViewModel()
    {
    }

    /// <summary>
    /// Creates one initialized scope upsert request.
    /// </summary>
    public PolicyScopeUpsertRequestViewModel(
        string scopeId,
        string? tenantId,
        string? organizationId,
        bool viewerRoleRequired,
        bool moderatorOverrideEnabled,
        bool adminOverrideEnabled,
        bool isActive = true)
    {
        ScopeId = scopeId;
        TenantId = tenantId;
        OrganizationId = organizationId;
        ViewerRoleRequired = viewerRoleRequired;
        ModeratorOverrideEnabled = moderatorOverrideEnabled;
        AdminOverrideEnabled = adminOverrideEnabled;
        IsActive = isActive;
    }

    /// <summary>
    /// Gets or sets the policy scope identifier.
    /// </summary>
    public string ScopeId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tenant identifier.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the organization identifier.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Gets or sets whether viewer access is required.
    /// </summary>
    public bool ViewerRoleRequired { get; set; }

    /// <summary>
    /// Gets or sets whether moderator override is enabled.
    /// </summary>
    public bool ModeratorOverrideEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether admin override is enabled.
    /// </summary>
    public bool AdminOverrideEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether the policy scope is active.
    /// </summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Represents one policy assignment upsert request sent by the operator shell.
/// </summary>
public sealed class PolicyAssignmentUpsertRequestViewModel
{
    /// <summary>
    /// Creates an empty assignment upsert request.
    /// </summary>
    public PolicyAssignmentUpsertRequestViewModel()
    {
    }

    /// <summary>
    /// Creates one initialized assignment upsert request.
    /// </summary>
    public PolicyAssignmentUpsertRequestViewModel(
        string? assignmentId,
        string scopeId,
        string principalId,
        IReadOnlyList<string> roles,
        string? source,
        bool isActive = true)
    {
        AssignmentId = assignmentId;
        ScopeId = scopeId;
        PrincipalId = principalId;
        Roles = roles;
        Source = source;
        IsActive = isActive;
    }

    /// <summary>
    /// Gets or sets the assignment identifier.
    /// </summary>
    public string? AssignmentId { get; set; }

    /// <summary>
    /// Gets or sets the policy scope identifier.
    /// </summary>
    public string ScopeId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the principal identifier.
    /// </summary>
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the granted roles.
    /// </summary>
    public IReadOnlyList<string> Roles { get; set; } = [];

    /// <summary>
    /// Gets or sets the source label.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets whether the assignment is active.
    /// </summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Represents the latest operational artifact groups shown in Ops Status.
/// </summary>
public sealed record OperationalArtifactSummaryViewModel(
    string PlatformRoot,
    OperationalArtifactGroupViewModel? Doctor,
    OperationalArtifactGroupViewModel? CiLocal,
    OperationalArtifactGroupViewModel? Checks,
    OperationalArtifactGroupViewModel? Smoke,
    OperationalArtifactGroupViewModel? Full,
    OperationalArtifactGroupViewModel? TokenDebug,
    OperationalArtifactGroupViewModel? BearerRollout,
    OperationalArtifactGroupViewModel? IdentityReset);

/// <summary>
/// Represents one artifact category and its latest run folder.
/// </summary>
public sealed record OperationalArtifactGroupViewModel(
    string DisplayName,
    string RunId,
    string RelativeDirectoryPath,
    DateTimeOffset UpdatedAtUtc,
    OperationalArtifactLinkViewModel? PreferredLink,
    IReadOnlyList<OperationalArtifactLinkViewModel> Links);

/// <summary>
/// Represents one downloadable operational artifact file.
/// </summary>
public sealed record OperationalArtifactLinkViewModel(
    string Name,
    string RelativePath,
    DateTimeOffset UpdatedAtUtc,
    long SizeBytes);
