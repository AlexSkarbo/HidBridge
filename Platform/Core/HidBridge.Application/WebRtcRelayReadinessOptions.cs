using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Application;

/// <summary>
/// Configures policy thresholds used by <see cref="WebRtcRelayReadinessService"/>.
/// </summary>
public sealed class WebRtcRelayReadinessOptions
{
    /// <summary>
    /// Gets or sets minimum required number of online relay peers.
    /// </summary>
    public int MinOnlinePeerCount { get; init; } = 1;

    /// <summary>
    /// Gets or sets peer states considered healthy for readiness.
    /// </summary>
    public IReadOnlySet<string> HealthyPeerStates { get; init; }
        = new HashSet<string>(new[] { "Connected", "Degraded" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets provider used by default when readiness is requested without explicit override.
    /// </summary>
    public RealtimeTransportProvider DefaultProvider { get; init; } = RealtimeTransportProvider.WebRtcDataChannel;
}
