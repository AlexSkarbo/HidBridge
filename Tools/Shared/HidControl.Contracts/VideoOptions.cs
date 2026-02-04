namespace HidControl.Contracts;

/// <summary>
/// Minimal video-related options used by URL/build helpers and infrastructure services.
/// </summary>
/// <param name="Url">Base server URL for video endpoints.</param>
/// <param name="BindAll">Whether to bind to all interfaces.</param>
/// <param name="VideoRtspPort">RTSP listen port.</param>
/// <param name="VideoSrtBasePort">Base SRT port for sources.</param>
/// <param name="VideoSrtLatencyMs">SRT latency in milliseconds.</param>
/// <param name="VideoRtmpPort">RTMP listen port.</param>
/// <param name="VideoHlsDir">Local HLS output directory.</param>
/// <param name="VideoLlHls">Enables low-latency HLS.</param>
/// <param name="VideoHlsSegmentSeconds">HLS segment duration seconds.</param>
/// <param name="VideoHlsListSize">HLS playlist size.</param>
/// <param name="VideoHlsDeleteSegments">Whether to delete old HLS segments.</param>
public sealed record VideoOptions(
    string Url,
    bool BindAll,
    int VideoRtspPort,
    int VideoSrtBasePort,
    int VideoSrtLatencyMs,
    int VideoRtmpPort,
    string VideoHlsDir,
    bool VideoLlHls,
    int VideoHlsSegmentSeconds,
    int VideoHlsListSize,
    bool VideoHlsDeleteSegments);
