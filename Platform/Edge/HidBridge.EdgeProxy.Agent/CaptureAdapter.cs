using HidBridge.Edge.Abstractions;

namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Defines media-capture adapter boundary used by edge proxy worker orchestration.
/// </summary>
public interface ICaptureAdapter
{
    /// <summary>
    /// Reads current capture readiness snapshot.
    /// </summary>
    Task<EdgeMediaReadinessSnapshot> GetReadinessAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Adapts <see cref="IEdgeMediaReadinessProbe"/> into one explicit capture adapter boundary.
/// </summary>
public sealed class CaptureAdapter : ICaptureAdapter
{
    private readonly IEdgeMediaReadinessProbe _probe;

    /// <summary>
    /// Initializes a new capture adapter instance.
    /// </summary>
    public CaptureAdapter(IEdgeMediaReadinessProbe probe)
    {
        _probe = probe;
    }

    /// <summary>
    /// Returns the latest probe snapshot.
    /// </summary>
    public Task<EdgeMediaReadinessSnapshot> GetReadinessAsync(CancellationToken cancellationToken)
        => _probe.GetReadinessAsync(cancellationToken);
}
