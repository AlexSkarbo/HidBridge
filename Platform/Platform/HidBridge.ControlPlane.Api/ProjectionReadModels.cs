using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Represents one paged projection result returned by optimized query endpoints.
/// </summary>
/// <typeparam name="TItem">The item type carried by the page.</typeparam>
public sealed record ProjectionPage<TItem>(
    int Total,
    int Skip,
    int Take,
    IReadOnlyList<TItem> Items);

/// <summary>
/// Represents one flattened session projection optimized for operator queries.
/// </summary>
public sealed record SessionProjectionItemReadModel(
    string SessionId,
    string AgentId,
    string EndpointId,
    string? TenantId,
    string? OrganizationId,
    SessionProfile Profile,
    SessionRole Role,
    SessionState State,
    string RequestedBy,
    int ParticipantCount,
    int ShareCount,
    int AcceptedShareCount,
    string? ActiveControllerPrincipalId,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc);

/// <summary>
/// Represents one flattened endpoint projection optimized for operator queries.
/// </summary>
public sealed record EndpointProjectionItemReadModel(
    string EndpointId,
    string? AgentId,
    ConnectorType? ConnectorType,
    string Status,
    int CapabilityCount,
    IReadOnlyList<string> CapabilityNames,
    string? ActiveSessionId,
    SessionState? ActiveSessionState,
    string? ActiveControllerPrincipalId,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Represents one audit projection row optimized for filtered operator feeds.
/// </summary>
public sealed record AuditProjectionItemReadModel(
    string Category,
    string Message,
    string? SessionId,
    string? PrincipalId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, object?>? Data);

/// <summary>
/// Represents one telemetry projection row optimized for filtered operator feeds.
/// </summary>
public sealed record TelemetryProjectionItemReadModel(
    string Scope,
    string? SessionId,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, string> Metrics,
    IReadOnlyDictionary<string, double> NumericMetrics);
