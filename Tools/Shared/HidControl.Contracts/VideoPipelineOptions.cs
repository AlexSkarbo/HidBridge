namespace HidControl.Contracts;

/// <summary>
/// Video pipeline options used for FFmpeg input/output decisions.
/// </summary>
/// <param name="Url">Base server URL for video endpoints.</param>
/// <param name="BindAll">Whether to bind to all interfaces.</param>
/// <param name="FfmpegPath">FFmpeg executable path.</param>
/// <param name="VideoRtspPort">RTSP listen port.</param>
/// <param name="VideoSrtBasePort">Base SRT port for sources.</param>
/// <param name="VideoSrtLatencyMs">SRT latency in milliseconds.</param>
/// <param name="VideoRtmpPort">RTMP listen port.</param>
/// <param name="VideoHlsDir">Local HLS output directory.</param>
/// <param name="VideoLlHls">Enables low-latency HLS.</param>
/// <param name="VideoHlsSegmentSeconds">HLS segment duration seconds.</param>
/// <param name="VideoHlsListSize">HLS playlist size.</param>
/// <param name="VideoHlsDeleteSegments">Whether to delete old HLS segments.</param>
/// <param name="VideoSecondaryCaptureEnabled">Enables secondary capture path.</param>
/// <param name="VideoModesCacheTtlSeconds">Video modes cache TTL in seconds.</param>
/// <param name="VideoKillOrphanFfmpeg">Whether to detect/kill orphan FFmpeg processes.</param>
public sealed record VideoPipelineOptions(
    string Url,
    bool BindAll,
    string FfmpegPath,
    int VideoRtspPort,
    int VideoSrtBasePort,
    int VideoSrtLatencyMs,
    int VideoRtmpPort,
    string VideoHlsDir,
    bool VideoLlHls,
    int VideoHlsSegmentSeconds,
    int VideoHlsListSize,
    bool VideoHlsDeleteSegments,
    bool VideoSecondaryCaptureEnabled,
    int VideoModesCacheTtlSeconds,
    bool VideoKillOrphanFfmpeg)
{
    /// <summary>
    /// Maps pipeline options to URL/video options DTO.
    /// </summary>
    /// <returns>Video options DTO.</returns>
    public VideoOptions ToVideoOptions()
    {
        return new VideoOptions(
            Url,
            BindAll,
            VideoRtspPort,
            VideoSrtBasePort,
            VideoSrtLatencyMs,
            VideoRtmpPort,
            VideoHlsDir,
            VideoLlHls,
            VideoHlsSegmentSeconds,
            VideoHlsListSize,
            VideoHlsDeleteSegments);
    }
}
