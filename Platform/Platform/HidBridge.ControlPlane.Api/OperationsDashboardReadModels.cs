using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Represents the top-level fleet dashboard used by control-plane operators.
/// </summary>
public sealed record InventoryDashboardReadModel(
    DateTimeOffset GeneratedAtUtc,
    int TotalAgents,
    int TotalEndpoints,
    int TotalSessions,
    int ActiveSessions,
    int RecoveringSessions,
    int FailedSessions,
    int ActiveControlLeases,
    IReadOnlyList<ConnectorTypeSummaryReadModel> ConnectorTypes,
    IReadOnlyList<InventoryEndpointCardReadModel> Endpoints);

/// <summary>
/// Represents one connector type bucket within the fleet dashboard.
/// </summary>
public sealed record ConnectorTypeSummaryReadModel(
    ConnectorType ConnectorType,
    int Count);

/// <summary>
/// Represents one endpoint card inside the fleet overview dashboard.
/// </summary>
public sealed record InventoryEndpointCardReadModel(
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
/// Represents the audit dashboard for operator diagnostics.
/// </summary>
public sealed record AuditDashboardReadModel(
    DateTimeOffset GeneratedAtUtc,
    int TotalEvents,
    int SessionScopedEvents,
    int GlobalEvents,
    DateTimeOffset? LatestEventAtUtc,
    IReadOnlyList<AuditCategorySummaryReadModel> Categories,
    IReadOnlyList<SessionAuditSummaryReadModel> Sessions,
    IReadOnlyList<AuditRecentEntryReadModel> RecentEntries);

/// <summary>
/// Represents one audit category bucket.
/// </summary>
public sealed record AuditCategorySummaryReadModel(
    string Category,
    int Count);

/// <summary>
/// Represents one per-session audit summary.
/// </summary>
public sealed record SessionAuditSummaryReadModel(
    string SessionId,
    int Count,
    DateTimeOffset? LatestEventAtUtc);

/// <summary>
/// Represents one recent audit entry for diagnostics dashboards.
/// </summary>
public sealed record AuditRecentEntryReadModel(
    string Category,
    string Message,
    string? SessionId,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Represents the telemetry dashboard for fleet diagnostics and stream health.
/// </summary>
public sealed record TelemetryDashboardReadModel(
    DateTimeOffset GeneratedAtUtc,
    int TotalEvents,
    int SessionScopedEvents,
    DateTimeOffset? LatestEventAtUtc,
    IReadOnlyList<TelemetryScopeSummaryReadModel> Scopes,
    IReadOnlyList<TelemetrySignalReadModel> Signals,
    IReadOnlyList<TelemetrySessionHealthReadModel> Sessions);

/// <summary>
/// Represents one telemetry scope bucket.
/// </summary>
public sealed record TelemetryScopeSummaryReadModel(
    string Scope,
    int Count,
    DateTimeOffset? LatestEventAtUtc);

/// <summary>
/// Represents one aggregated telemetry signal.
/// </summary>
public sealed record TelemetrySignalReadModel(
    string Scope,
    string MetricName,
    string LatestValue,
    double? LatestNumericValue,
    DateTimeOffset LatestAtUtc);

/// <summary>
/// Represents one telemetry-backed session health card.
/// </summary>
public sealed record TelemetrySessionHealthReadModel(
    string SessionId,
    int EventCount,
    DateTimeOffset? LatestEventAtUtc,
    IReadOnlyDictionary<string, string> LatestMetrics);
