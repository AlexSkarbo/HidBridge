namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Selects media runtime engine used by edge proxy worker.
/// </summary>
public enum EdgeProxyMediaEngineKind
{
    /// <summary>
    /// No dedicated media runtime process management.
    /// </summary>
    None,

    /// <summary>
    /// Preview ffmpeg + DataChannelDotNet media runtime with process orchestration.
    /// </summary>
    FfmpegDataChannelDotNet,
}
