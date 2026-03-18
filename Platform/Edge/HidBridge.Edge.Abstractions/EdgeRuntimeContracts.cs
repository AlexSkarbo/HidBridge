namespace HidBridge.Edge.Abstractions;

/// <summary>
/// Represents one device-facing health snapshot collected by edge runtime components.
/// </summary>
public sealed record EdgeDeviceHealthSnapshot(
    bool IsConnected,
    string Status,
    DateTimeOffset ReportedAtUtc,
    IReadOnlyDictionary<string, object?> Metrics);

/// <summary>
/// Provides low-level target-device health diagnostics for edge runtime orchestration.
/// </summary>
public interface IEdgeDeviceHealthProbe
{
    /// <summary>
    /// Reads current device-facing health snapshot.
    /// </summary>
    Task<EdgeDeviceHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Represents one telemetry payload emitted by edge runtime.
/// </summary>
public sealed record EdgeTelemetryEvent(
    string Name,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>
/// Publishes edge telemetry events toward platform ingestion endpoints.
/// </summary>
public interface IEdgeTelemetryPublisher
{
    /// <summary>
    /// Publishes one telemetry event.
    /// </summary>
    Task PublishAsync(EdgeTelemetryEvent telemetryEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Represents one media-related edge signal (stream status, source health, bitrate notes, etc.).
/// </summary>
public sealed record EdgeMediaSignal(
    string StreamId,
    string State,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, object?> Metadata);

/// <summary>
/// Publishes edge media signals toward platform media orchestration endpoints.
/// </summary>
public interface IEdgeMediaPublisher
{
    /// <summary>
    /// Publishes one media signal.
    /// </summary>
    Task PublishAsync(EdgeMediaSignal signal, CancellationToken cancellationToken);
}
