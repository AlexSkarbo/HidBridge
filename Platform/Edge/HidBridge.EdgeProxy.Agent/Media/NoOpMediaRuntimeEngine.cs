namespace HidBridge.EdgeProxy.Agent.Media;

/// <summary>
/// No-op media runtime engine used by default to preserve existing runtime behavior.
/// </summary>
internal sealed class NoOpMediaRuntimeEngine : IEdgeMediaRuntimeEngine
{
    private EdgeMediaRuntimeSnapshot _snapshot = new(
        IsRunning: false,
        State: "NotConfigured",
        ReportedAtUtc: DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public Task StartAsync(EdgeMediaRuntimeContext context, CancellationToken cancellationToken)
    {
        _snapshot = new EdgeMediaRuntimeSnapshot(
            IsRunning: false,
            State: "NotConfigured",
            ReportedAtUtc: DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _snapshot = new EdgeMediaRuntimeSnapshot(
            IsRunning: false,
            State: "Stopped",
            ReportedAtUtc: DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<EdgeMediaRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => Task.FromResult(_snapshot);
}
