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

    /// <summary>
    /// Gets or sets whether readiness policy requires media path to report ready.
    /// </summary>
    public bool RequireMediaReady { get; init; }

    /// <summary>
    /// Gets or sets media states considered healthy when <see cref="RequireMediaReady"/> is enabled.
    /// </summary>
    public IReadOnlySet<string> HealthyMediaStates { get; init; }
        = new HashSet<string>(new[] { "Ready", "Streaming", "Connected", "Healthy" }, StringComparer.OrdinalIgnoreCase);
}
