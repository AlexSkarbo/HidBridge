namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Represents one SLO alert emitted by transport diagnostics.
/// </summary>
public sealed record TransportSloAlertReadModel(
    string Metric,
    string Severity,
    double Observed,
    double Threshold,
    string Message);

/// <summary>
/// Represents one per-session transport SLO projection row.
/// </summary>
public sealed record TransportSloSessionReadModel(
    string SessionId,
    string EndpointId,
    int OnlinePeerCount,
    string? LastPeerState,
    DateTimeOffset? LastPeerSeenAtUtc,
    DateTimeOffset? LastRelayAckAtUtc,
    double? RelayReadyLatencyMs,
    int ReconnectTransitionCount,
    double ReconnectFrequencyPerHour);

/// <summary>
/// Represents active threshold values used to classify transport SLO health.
/// </summary>
public sealed record TransportSloThresholdsReadModel(
    double RelayReadyLatencyWarnMs,
    double RelayReadyLatencyCriticalMs,
    double AckTimeoutRateWarn,
    double AckTimeoutRateCritical,
    double ReconnectFrequencyWarnPerHour,
    double ReconnectFrequencyCriticalPerHour);

/// <summary>
/// Represents alert counters by severity.
/// </summary>
public sealed record TransportSloAlertCountersReadModel(
    int WarningCount,
    int CriticalCount);

/// <summary>
/// Represents explicit threshold-breach flags for primary transport SLO metrics.
/// </summary>
public sealed record TransportSloBreachReadModel(
    bool AckTimeoutRateWarn,
    bool AckTimeoutRateCritical,
    bool RelayReadyLatencyP95Warn,
    bool RelayReadyLatencyP95Critical,
    bool ReconnectFrequencyWarnPerHour,
    bool ReconnectFrequencyCriticalPerHour);

/// <summary>
/// Represents aggregated transport SLO summary for one diagnostics window.
/// </summary>
public sealed record TransportSloSummaryReadModel(
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset WindowStartUtc,
    int WindowMinutes,
    int SessionCount,
    int RelayCommandCount,
    int RelayTimeoutCount,
    double AckTimeoutRate,
    double? RelayReadyLatencyP50Ms,
    double? RelayReadyLatencyP95Ms,
    double ReconnectFrequencyPerHour,
    string Status,
    TransportSloAlertCountersReadModel AlertCounters,
    TransportSloBreachReadModel Breaches,
    IReadOnlyList<TransportSloAlertReadModel> Alerts,
    IReadOnlyList<TransportSloSessionReadModel> Sessions,
    TransportSloThresholdsReadModel Thresholds);
