namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Configures transport SLO diagnostics windows and alert thresholds.
/// </summary>
public sealed class TransportSloDiagnosticsOptions
{
    /// <summary>
    /// Gets or sets default rolling window (minutes) used when query does not specify one.
    /// </summary>
    public int DefaultWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets warning threshold for relay-ready latency.
    /// </summary>
    public double RelayReadyLatencyWarnMs { get; set; } = 15_000d;

    /// <summary>
    /// Gets or sets critical threshold for relay-ready latency.
    /// </summary>
    public double RelayReadyLatencyCriticalMs { get; set; } = 45_000d;

    /// <summary>
    /// Gets or sets warning threshold for ACK timeout rate.
    /// </summary>
    public double AckTimeoutRateWarn { get; set; } = 0.05d;

    /// <summary>
    /// Gets or sets critical threshold for ACK timeout rate.
    /// </summary>
    public double AckTimeoutRateCritical { get; set; } = 0.20d;

    /// <summary>
    /// Gets or sets warning threshold for reconnect frequency.
    /// </summary>
    public double ReconnectFrequencyWarnPerHour { get; set; } = 2d;

    /// <summary>
    /// Gets or sets critical threshold for reconnect frequency.
    /// </summary>
    public double ReconnectFrequencyCriticalPerHour { get; set; } = 5d;

    /// <summary>
    /// Clamps options to safe and internally consistent bounds.
    /// </summary>
    public void Normalize()
    {
        DefaultWindowMinutes = Math.Clamp(DefaultWindowMinutes, 5, 24 * 60);
        RelayReadyLatencyWarnMs = Math.Max(1_000d, RelayReadyLatencyWarnMs);
        RelayReadyLatencyCriticalMs = Math.Max(RelayReadyLatencyWarnMs, RelayReadyLatencyCriticalMs);
        AckTimeoutRateWarn = Math.Clamp(AckTimeoutRateWarn, 0d, 1d);
        AckTimeoutRateCritical = Math.Clamp(AckTimeoutRateCritical, AckTimeoutRateWarn, 1d);
        ReconnectFrequencyWarnPerHour = Math.Max(0d, ReconnectFrequencyWarnPerHour);
        ReconnectFrequencyCriticalPerHour = Math.Max(ReconnectFrequencyWarnPerHour, ReconnectFrequencyCriticalPerHour);
    }
}
