using HidControl.Contracts;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Builds bind and stream URLs for video endpoints.
/// </summary>
/// <summary>
/// Builds bind and stream URLs for video endpoints.
/// </summary>
public static class VideoUrlService
{
    /// <summary>
    /// Builds server bind URLs from video options.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <returns>Bind URLs.</returns>
    public static string[] BuildBindUrls(VideoOptions opt)
    {
        if (!opt.BindAll)
        {
            return new[] { opt.Url };
        }
        if (opt.Url.Contains("://"))
        {
            Uri uri = new(opt.Url);
            return new[]
            {
                $"{uri.Scheme}://0.0.0.0:{uri.Port}",
                opt.Url
            };
        }
        return new[] { opt.Url };
    }

    /// <summary>
    /// Gets the host for video endpoints based on video options.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <returns>Host name.</returns>
    public static string GetVideoHost(VideoOptions opt)
    {
        if (opt.Url.Contains("://"))
        {
            Uri uri = new(opt.Url);
            return uri.Host == "0.0.0.0" ? "127.0.0.1" : uri.Host;
        }
        return "127.0.0.1";
    }

    /// <summary>
    /// Gets the base URL for video endpoints using video options.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <returns>Base URL.</returns>
    public static string GetVideoBaseUrl(VideoOptions opt)
    {
        if (opt.Url.Contains("://"))
        {
            return opt.Url.TrimEnd('/');
        }
        return $"http://{GetVideoHost(opt)}:8080";
    }

    /// <summary>
    /// Builds an RTSP listen URL.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Listen URL.</returns>
    public static string BuildRtspListenUrl(VideoOptions opt, string id)
    {
        return $"rtsp://0.0.0.0:{opt.VideoRtspPort}/{id}";
    }

    /// <summary>
    /// Builds an RTSP client URL.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Client URL.</returns>
    public static string BuildRtspClientUrl(VideoOptions opt, string id)
    {
        return $"rtsp://{GetVideoHost(opt)}:{opt.VideoRtspPort}/{id}";
    }

    /// <summary>
    /// Builds an SRT port for a source index.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <param name="index">Source index.</param>
    /// <returns>Port number.</returns>
    public static int BuildSrtPort(VideoOptions opt, int index)
    {
        return opt.VideoSrtBasePort + index;
    }

    /// <summary>
    /// Builds an SRT listen URL.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <param name="index">Source index.</param>
    /// <returns>Listen URL.</returns>
    public static string BuildSrtListenUrl(VideoOptions opt, int index)
    {
        int port = BuildSrtPort(opt, index);
        return $"srt://0.0.0.0:{port}?mode=listener&transtype=live&latency={opt.VideoSrtLatencyMs}";
    }

    /// <summary>
    /// Builds an SRT client URL.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <param name="index">Source index.</param>
    /// <returns>Client URL.</returns>
    public static string BuildSrtClientUrl(VideoOptions opt, int index)
    {
        int port = BuildSrtPort(opt, index);
        return $"srt://{GetVideoHost(opt)}:{port}?mode=caller&transtype=live&latency={opt.VideoSrtLatencyMs}";
    }

    /// <summary>
    /// Builds an RTMP listen URL.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Listen URL.</returns>
    public static string BuildRtmpListenUrl(VideoOptions opt, string id)
    {
        return $"rtmp://0.0.0.0:{opt.VideoRtmpPort}/live/{id}";
    }

    /// <summary>
    /// Builds an RTMP client URL.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Client URL.</returns>
    public static string BuildRtmpClientUrl(VideoOptions opt, string id)
    {
        return $"rtmp://{GetVideoHost(opt)}:{opt.VideoRtmpPort}/live/{id}";
    }

    /// <summary>
    /// Builds a local HLS output directory.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Directory path.</returns>
    public static string BuildHlsDir(VideoOptions opt, string id)
    {
        return Path.Combine(opt.VideoHlsDir, id);
    }

    /// <summary>
    /// Builds an HLS playback URL.
    /// </summary>
    /// <param name="opt">Video options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>HLS URL.</returns>
    public static string BuildHlsUrl(VideoOptions opt, string id)
    {
        return $"{GetVideoBaseUrl(opt)}/video/hls/{id}/index.m3u8";
    }
}
