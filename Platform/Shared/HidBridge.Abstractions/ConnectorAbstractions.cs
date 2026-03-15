using HidBridge.Contracts;

namespace HidBridge.Abstractions;

/// <summary>
/// Describes one registered connector instance exposed by the control plane.
/// </summary>
public sealed record ConnectorDescriptor(
    string AgentId,
    string EndpointId,
    ConnectorType ConnectorType,
    IReadOnlyList<CapabilityDescriptor> Capabilities);

/// <summary>
/// Captures the result returned by a connector after executing a command.
/// </summary>
public sealed record ConnectorCommandResult(
    string CommandId,
    CommandStatus Status,
    ErrorInfo? Error = null,
    IReadOnlyDictionary<string, double>? Metrics = null);

/// <summary>
/// Identifies the transport provider used by the realtime transport fabric.
/// </summary>
public enum RealtimeTransportProvider
{
    Uart,
    WebRtcDataChannel,
}

/// <summary>
/// Represents one normalized realtime transport message category.
/// </summary>
public enum RealtimeTransportMessageKind
{
    Command,
    Ack,
    Event,
    Heartbeat,
}

/// <summary>
/// Represents one transport-level error class independent of the concrete provider.
/// </summary>
public enum RealtimeTransportErrorKind
{
    Timeout,
    Disconnected,
    Rejected,
    ProtocolError,
}

/// <summary>
/// Carries route metadata used by realtime transport providers.
/// </summary>
public sealed record RealtimeTransportRouteContext(
    string AgentId,
    string? EndpointId = null,
    string? SessionId = null,
    string? RoomId = null,
    string? AttachmentId = null);

/// <summary>
/// Represents one normalized transport message payload.
/// </summary>
public sealed record RealtimeTransportMessage(
    RealtimeTransportMessageKind Kind,
    string Name,
    IReadOnlyDictionary<string, object?> Payload,
    DateTimeOffset? CreatedAtUtc = null);

/// <summary>
/// Represents one transport health snapshot for diagnostics and routing decisions.
/// </summary>
public sealed record RealtimeTransportHealth(
    RealtimeTransportProvider Provider,
    bool IsConnected,
    string Status,
    IReadOnlyDictionary<string, object?> Metrics);

/// <summary>
/// Defines one provider adapter that can connect, send commands, receive events, and report health.
/// </summary>
public interface IRealtimeTransport
{
    /// <summary>
    /// Gets the provider identity implemented by this adapter.
    /// </summary>
    RealtimeTransportProvider Provider { get; }

    /// <summary>
    /// Ensures provider-level connectivity for the supplied route context.
    /// </summary>
    Task ConnectAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one command and returns the normalized command acknowledgment.
    /// </summary>
    Task<CommandAckBody> SendCommandAsync(RealtimeTransportRouteContext route, CommandRequestBody command, CancellationToken cancellationToken);

    /// <summary>
    /// Reads provider-originated event/heartbeat messages for the supplied route context.
    /// </summary>
    IAsyncEnumerable<RealtimeTransportMessage> ReceiveAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken);

    /// <summary>
    /// Closes provider-level connectivity for the supplied route context.
    /// </summary>
    Task CloseAsync(RealtimeTransportRouteContext route, string? reason, CancellationToken cancellationToken);

    /// <summary>
    /// Reads provider-level health details for the supplied route context.
    /// </summary>
    Task<RealtimeTransportHealth> GetHealthAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves a concrete realtime transport provider based on runtime policy.
/// </summary>
public interface IRealtimeTransportFactory
{
    /// <summary>
    /// Gets the default provider selected by runtime configuration.
    /// </summary>
    RealtimeTransportProvider DefaultProvider { get; }

    /// <summary>
    /// Resolves one concrete transport provider.
    /// </summary>
    IRealtimeTransport Resolve(RealtimeTransportProvider? provider = null);
}

/// <summary>
/// Defines the runtime contract implemented by every agent or agentless connector.
/// </summary>
public interface IConnector
{
    /// <summary>
    /// Gets the immutable descriptor of the connector instance.
    /// </summary>
    ConnectorDescriptor Descriptor { get; }

    /// <summary>
    /// Registers the connector and returns its advertised contract payload.
    /// </summary>
    Task<AgentRegisterBody> RegisterAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the current health snapshot of the connector.
    /// </summary>
    Task<AgentHeartbeatBody> HeartbeatAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Executes one command against the connector target.
    /// </summary>
    Task<ConnectorCommandResult> ExecuteAsync(CommandRequestBody command, CancellationToken cancellationToken);
}

/// <summary>
/// Stores and resolves active connector instances.
/// </summary>
public interface IConnectorRegistry
{
    /// <summary>
    /// Adds or replaces a connector in the registry.
    /// </summary>
    Task RegisterAsync(IConnector connector, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the descriptors of all known connectors.
    /// </summary>
    Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a connector instance by agent identifier.
    /// </summary>
    Task<IConnector?> ResolveAsync(string agentId, CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates session lifecycle and command dispatch across connectors.
/// </summary>
public interface ISessionOrchestrator
{
    /// <summary>
    /// Opens a new session.
    /// </summary>
    Task<SessionOpenBody> OpenAsync(SessionOpenBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Closes an existing session.
    /// </summary>
    Task<SessionCloseBody> CloseAsync(SessionCloseBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Dispatches a command through the session orchestration layer.
    /// </summary>
    Task<CommandAckBody> DispatchAsync(CommandRequestBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the current participants attached to the specified session.
    /// </summary>
    Task<IReadOnlyList<SessionParticipantBody>> ListParticipantsAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or updates a session participant.
    /// </summary>
    Task<SessionParticipantBody> UpsertParticipantAsync(string sessionId, SessionParticipantUpsertBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a participant from the specified session.
    /// </summary>
    Task<SessionParticipantRemoveBody> RemoveParticipantAsync(string sessionId, SessionParticipantRemoveBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the share grants tracked for the specified session.
    /// </summary>
    Task<IReadOnlyList<SessionShareBody>> ListSharesAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Grants a new share invitation for the specified session.
    /// </summary>
    Task<SessionShareBody> GrantShareAsync(string sessionId, SessionShareGrantBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Records a new invitation request that must be approved before it becomes a share invitation.
    /// </summary>
    Task<SessionShareBody> RequestInvitationAsync(string sessionId, SessionInvitationRequestBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Approves a requested invitation and converts it into a pending share invitation.
    /// </summary>
    Task<SessionShareBody> ApproveInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Declines a requested invitation without materializing a participant.
    /// </summary>
    Task<SessionShareBody> DeclineInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Accepts a pending share and materializes it as a participant.
    /// </summary>
    Task<SessionShareBody> AcceptShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Rejects a pending share grant.
    /// </summary>
    Task<SessionShareBody> RejectShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes an existing share grant.
    /// </summary>
    Task<SessionShareBody> RevokeShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the currently active control lease for the specified session.
    /// </summary>
    Task<SessionControlLeaseBody?> GetControlLeaseAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Requests control for one participant and acquires it when no competing controller is active.
    /// </summary>
    Task<SessionControlLeaseBody> RequestControlAsync(string sessionId, SessionControlRequestBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Grants control to one participant under moderator policy rules.
    /// </summary>
    Task<SessionControlLeaseBody> GrantControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the currently active control lease.
    /// </summary>
    Task<SessionControlLeaseBody?> ReleaseControlAsync(string sessionId, SessionControlReleaseBody request, CancellationToken cancellationToken);

    /// <summary>
    /// Force-transfers control to one participant regardless of the current controller.
    /// </summary>
    Task<SessionControlLeaseBody> ForceTakeoverControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken);
}

/// <summary>
/// Persists audit and telemetry events emitted by the platform.
/// </summary>
public interface IEventWriter
{
    /// <summary>
    /// Writes one audit event.
    /// </summary>
    Task WriteAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Writes one telemetry event.
    /// </summary>
    Task WriteTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Tracks the endpoint inventory known to the platform.
/// </summary>
public interface IEndpointInventory
{
    /// <summary>
    /// Creates or updates one endpoint capability snapshot.
    /// </summary>
    Task UpsertEndpointAsync(string endpointId, IReadOnlyList<CapabilityDescriptor> capabilities, CancellationToken cancellationToken);

    /// <summary>
    /// Lists known endpoint identifiers.
    /// </summary>
    Task<IReadOnlyList<string>> ListEndpointsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents one persisted endpoint snapshot together with its latest advertised capabilities.
/// </summary>
public sealed record EndpointSnapshot(
    string EndpointId,
    IReadOnlyList<CapabilityDescriptor> Capabilities,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Represents one active session participant persisted by the platform.
/// </summary>
public sealed record SessionParticipantSnapshot(
    string ParticipantId,
    string PrincipalId,
    SessionRole Role,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Represents one persisted share grant for a session.
/// </summary>
public sealed record SessionShareSnapshot(
    string ShareId,
    string PrincipalId,
    string GrantedBy,
    SessionRole Role,
    SessionShareStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Represents one persisted session snapshot.
/// </summary>
public sealed record SessionSnapshot(
    string SessionId,
    string AgentId,
    string EndpointId,
    SessionProfile Profile,
    string RequestedBy,
    SessionRole Role,
    SessionState State,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<SessionParticipantSnapshot>? Participants = null,
    IReadOnlyList<SessionShareSnapshot>? Shares = null,
    SessionControlLeaseBody? ControlLease = null,
    DateTimeOffset? LastHeartbeatAtUtc = null,
    DateTimeOffset? LeaseExpiresAtUtc = null,
    string? TenantId = null,
    string? OrganizationId = null);

/// <summary>
/// Persists connector descriptors that survive process restarts.
/// </summary>
public interface IConnectorCatalogStore
{
    /// <summary>
    /// Creates or updates one connector descriptor snapshot.
    /// </summary>
    Task UpsertAsync(ConnectorDescriptor descriptor, CancellationToken cancellationToken);

    /// <summary>
    /// Lists all persisted connector descriptors.
    /// </summary>
    Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Persists endpoint snapshots and exposes read access for API queries.
/// </summary>
public interface IEndpointSnapshotStore
{
    /// <summary>
    /// Creates or updates one endpoint snapshot.
    /// </summary>
    Task UpsertAsync(EndpointSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Lists all persisted endpoint snapshots.
    /// </summary>
    Task<IReadOnlyList<EndpointSnapshot>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Persists audit and telemetry streams and exposes read access for diagnostics.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Appends one audit event to the persistent stream.
    /// </summary>
    Task AppendAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Appends one telemetry event to the persistent stream.
    /// </summary>
    Task AppendTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the persisted audit events in storage order.
    /// </summary>
    Task<IReadOnlyList<AuditEventBody>> ListAuditAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists the persisted telemetry events in storage order.
    /// </summary>
    Task<IReadOnlyList<TelemetryEventBody>> ListTelemetryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes audit events older than the specified cutoff and returns the number of deleted items.
    /// </summary>
    Task<int> TrimAuditAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Removes telemetry events older than the specified cutoff and returns the number of deleted items.
    /// </summary>
    Task<int> TrimTelemetryAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Persists session snapshots and exposes read access for API queries.
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Creates or updates one session snapshot.
    /// </summary>
    Task UpsertAsync(SessionSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one session snapshot from storage.
    /// </summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists all persisted session snapshots.
    /// </summary>
    Task<IReadOnlyList<SessionSnapshot>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents one persisted tenant/organization policy scope.
/// </summary>
public sealed record PolicyScopeSnapshot(
    string ScopeId,
    string? TenantId,
    string? OrganizationId,
    bool ViewerRoleRequired,
    bool ModeratorOverrideEnabled,
    bool AdminOverrideEnabled,
    DateTimeOffset UpdatedAtUtc,
    bool IsActive = true);

/// <summary>
/// Represents one persisted principal-to-policy assignment.
/// </summary>
public sealed record PolicyAssignmentSnapshot(
    string AssignmentId,
    string ScopeId,
    string PrincipalId,
    IReadOnlyList<string> Roles,
    DateTimeOffset UpdatedAtUtc,
    string? Source = null,
    bool IsActive = true);

/// <summary>
/// Persists tenant/organization policy scopes and principal assignments.
/// </summary>
public interface IPolicyStore
{
    /// <summary>
    /// Creates or updates one policy scope.
    /// </summary>
    Task UpsertScopeAsync(PolicyScopeSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or updates one policy assignment.
    /// </summary>
    Task UpsertAssignmentAsync(PolicyAssignmentSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one persisted policy scope.
    /// </summary>
    Task RemoveScopeAsync(string scopeId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes one persisted policy assignment.
    /// </summary>
    Task RemoveAssignmentAsync(string assignmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists all persisted policy scopes.
    /// </summary>
    Task<IReadOnlyList<PolicyScopeSnapshot>> ListScopesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists all persisted policy assignments.
    /// </summary>
    Task<IReadOnlyList<PolicyAssignmentSnapshot>> ListAssignmentsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents one versioned policy snapshot revision.
/// </summary>
public sealed record PolicyRevisionSnapshot(
    string RevisionId,
    string EntityType,
    string EntityId,
    string ScopeId,
    string? PrincipalId,
    int Version,
    string SnapshotJson,
    DateTimeOffset CreatedAtUtc,
    string Source);

/// <summary>
/// Persists versioned policy snapshots for diagnostics and audit.
/// </summary>
public interface IPolicyRevisionStore
{
    /// <summary>
    /// Appends one policy revision snapshot.
    /// </summary>
    Task AppendAsync(PolicyRevisionSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Lists persisted policy revisions in storage order.
    /// </summary>
    Task<IReadOnlyList<PolicyRevisionSnapshot>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Prunes revisions outside the retention window while keeping the newest entries per entity.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset retainSinceUtc, int maxRevisionsPerEntity, CancellationToken cancellationToken);
}

/// <summary>
/// Represents one resolved operator policy projection for a caller principal.
/// </summary>
public sealed record OperatorPolicyResolution(
    string? PrincipalId,
    string? TenantId,
    string? OrganizationId,
    IReadOnlyList<string> OperatorRoles,
    IReadOnlyList<PolicyAssignmentSnapshot> MatchedAssignments,
    IReadOnlyList<PolicyScopeSnapshot> MatchedScopes);

/// <summary>
/// Resolves effective tenant/organization scope and operator roles from persisted policy configuration.
/// </summary>
public interface IOperatorPolicyService
{
    /// <summary>
    /// Resolves the effective operator policy for the specified principal and optional incoming scope.
    /// </summary>
    Task<OperatorPolicyResolution> ResolveAsync(
        string? principalId,
        string? tenantId,
        string? organizationId,
        IReadOnlyList<string>? operatorRoles,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provides shared operator-role helpers used by API, projections, and orchestration layers.
/// </summary>
public static class OperatorPolicyRoles
{
    /// <summary>
    /// Gets the normalized HidBridge operator roles from one role set.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? roles)
        => roles is null
            ? []
            : roles
                .Where(static role => !string.IsNullOrWhiteSpace(role))
                .Where(static role => role.StartsWith("operator.", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static role => role, StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>
    /// Returns <see langword="true"/> when the role set grants viewer access.
    /// </summary>
    public static bool HasViewerAccess(IReadOnlyList<string>? roles)
    {
        if (roles is null)
        {
            return true;
        }

        var normalized = Normalize(roles);
        return normalized.Any(static role =>
                string.Equals(role, "operator.viewer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "operator.moderator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "operator.admin", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns <see langword="true"/> when the role set grants moderation access.
    /// </summary>
    public static bool HasModerationAccess(IReadOnlyList<string>? roles)
        => Normalize(roles).Any(static role =>
            string.Equals(role, "operator.moderator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "operator.admin", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns <see langword="true"/> when the role set grants administrator access.
    /// </summary>
    public static bool HasAdminAccess(IReadOnlyList<string>? roles)
        => Normalize(roles).Any(static role => string.Equals(role, "operator.admin", StringComparison.OrdinalIgnoreCase));
}


/// <summary>
/// Persists the command execution journal and exposes read access for diagnostics and collaboration timelines.
/// </summary>
public interface ICommandJournalStore
{
    /// <summary>
    /// Appends one command journal entry.
    /// </summary>
    Task AppendAsync(CommandJournalEntryBody entry, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves one persisted command journal entry by its globally unique command identifier.
    /// </summary>
    Task<CommandJournalEntryBody?> FindByCommandIdAsync(string commandId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves one persisted command journal entry by the session-scoped idempotency key.
    /// </summary>
    Task<CommandJournalEntryBody?> FindByIdempotencyKeyAsync(string sessionId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Lists all persisted command journal entries in storage order.
    /// </summary>
    Task<IReadOnlyList<CommandJournalEntryBody>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists persisted command journal entries for one session in storage order.
    /// </summary>
    Task<IReadOnlyList<CommandJournalEntryBody>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken);
}
