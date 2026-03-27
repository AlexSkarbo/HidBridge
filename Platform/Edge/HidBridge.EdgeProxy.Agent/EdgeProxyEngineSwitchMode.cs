namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Determines how edge worker chooses and promotes transport/media engines.
/// </summary>
public enum EdgeProxyEngineSwitchMode
{
    /// <summary>
    /// Uses explicitly configured engines as-is.
    /// </summary>
    Fixed,

    /// <summary>
    /// Runs DCD/ffmpeg engines in compatibility-first mode with forced relay fallback.
    /// </summary>
    Shadow,

    /// <summary>
    /// Enables DCD/ffmpeg only for a deterministic canary subset.
    /// </summary>
    Canary,

    /// <summary>
    /// Production default: DCD/ffmpeg enabled, with SLO-based auto-fallback.
    /// </summary>
    DefaultSwitch,
}

