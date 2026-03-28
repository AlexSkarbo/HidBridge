namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Preset latency/quality profiles for ffmpeg media publish path.
/// </summary>
public enum EdgeProxyFfmpegLatencyProfile
{
    /// <summary>
    /// Balanced quality/latency profile for day-to-day usage.
    /// </summary>
    Balanced,

    /// <summary>
    /// Aggressive low-latency profile.
    /// </summary>
    Ultra,

    /// <summary>
    /// Extreme low-latency profile (quality/stability trade-off).
    /// </summary>
    Extreme,
}
