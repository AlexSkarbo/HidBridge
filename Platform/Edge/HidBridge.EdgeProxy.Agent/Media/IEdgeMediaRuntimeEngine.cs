namespace HidBridge.EdgeProxy.Agent.Media;

/// <summary>
/// Defines pluggable media runtime engine contract for edge worker lifecycle orchestration.
/// </summary>
internal interface IEdgeMediaRuntimeEngine
{
    /// <summary>
    /// Starts media runtime engine.
    /// </summary>
    Task StartAsync(EdgeMediaRuntimeContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Stops media runtime engine and releases runtime resources.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns current engine runtime snapshot.
    /// </summary>
    Task<EdgeMediaRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Carries edge session identity and routing hints for media engine startup.
/// </summary>
internal sealed record EdgeMediaRuntimeContext(
    string SessionId,
    string PeerId,
    string EndpointId,
    string StreamId,
    string Source);

/// <summary>
/// Captures one runtime media-engine health snapshot used for diagnostics enrichment.
/// </summary>
internal sealed record EdgeMediaRuntimeSnapshot(
    bool IsRunning,
    string State,
    DateTimeOffset ReportedAtUtc,
    string? FailureReason = null,
    IReadOnlyDictionary<string, object?>? Metrics = null);
