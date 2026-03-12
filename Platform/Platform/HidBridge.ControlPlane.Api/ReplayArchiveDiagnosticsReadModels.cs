using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Represents one replay-oriented bundle for a session.
/// </summary>
public sealed record SessionReplayBundleReadModel(
    string SessionId,
    SessionSnapshot Session,
    int AuditCount,
    int TelemetryCount,
    int CommandCount,
    IReadOnlyList<AuditEventBody> Audit,
    IReadOnlyList<TelemetryEventBody> Telemetry,
    IReadOnlyList<CommandJournalEntryBody> Commands,
    IReadOnlyList<TimelineEntryBody> Timeline);

/// <summary>
/// Represents one archive diagnostics summary across persisted event streams and command journal data.
/// </summary>
public sealed record ArchiveDiagnosticsSummaryReadModel(
    DateTimeOffset GeneratedAtUtc,
    string? SessionId,
    DateTimeOffset? SinceUtc,
    int AuditCount,
    int TelemetryCount,
    int CommandCount,
    DateTimeOffset? EarliestAuditAtUtc,
    DateTimeOffset? LatestAuditAtUtc,
    DateTimeOffset? EarliestTelemetryAtUtc,
    DateTimeOffset? LatestTelemetryAtUtc,
    DateTimeOffset? EarliestCommandAtUtc,
    DateTimeOffset? LatestCommandAtUtc);
