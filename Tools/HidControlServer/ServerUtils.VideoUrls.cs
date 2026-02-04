using HidControlServer.Services;

namespace HidControlServer;

// URL builders and persistence helpers for video-related endpoints/config.
/// <summary>
/// Provides server utility helpers for videourls.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Builds bind urls.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <returns>Result.</returns>
    public static string[] BuildBindUrls(Options opt)
    {
        return VideoUrlService.BuildBindUrls(opt);
    }

    /// <summary>
    /// Gets video host.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <returns>Result.</returns>
    public static string GetVideoHost(Options opt)
    {
        return VideoUrlService.GetVideoHost(opt);
    }

    /// <summary>
    /// Gets video base url.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <returns>Result.</returns>
    public static string GetVideoBaseUrl(Options opt)
    {
        return VideoUrlService.GetVideoBaseUrl(opt);
    }

    /// <summary>
    /// Gets video source index.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <param name="src">The src.</param>
    /// <returns>Result.</returns>
    public static int GetVideoSourceIndex(IReadOnlyList<VideoSourceConfig> sources, VideoSourceConfig src)
    {
        return VideoConfigService.GetVideoSourceIndex(sources, src);
    }

    /// <summary>
    /// Tries to persist video sources.
    /// </summary>
    /// <param name="appState">The appState.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="error">The error.</param>
    /// <returns>Result.</returns>
    public static bool TryPersistVideoSources(AppState appState, IReadOnlyList<VideoSourceConfig> sources, out string? error)
    {
        return VideoConfigService.TryPersistVideoSources(appState, sources, out error);
    }

    /// <summary>
    /// Tries to persist active video profile.
    /// </summary>
    /// <param name="appState">The appState.</param>
    /// <param name="activeProfile">The activeProfile.</param>
    /// <param name="error">The error.</param>
    /// <returns>Result.</returns>
    public static bool TryPersistActiveVideoProfile(AppState appState, string activeProfile, out string? error)
    {
        return VideoConfigService.TryPersistActiveVideoProfile(appState, activeProfile, out error);
    }

    /// <summary>
    /// Builds rtsp listen url.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    public static string BuildRtspListenUrl(Options opt, string id)
    {
        return VideoUrlService.BuildRtspListenUrl(opt, id);
    }

    /// <summary>
    /// Builds rtsp client url.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    public static string BuildRtspClientUrl(Options opt, string id)
    {
        return VideoUrlService.BuildRtspClientUrl(opt, id);
    }

    /// <summary>
    /// Builds srt port.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="index">The index.</param>
    /// <returns>Result.</returns>
    public static int BuildSrtPort(Options opt, int index)
    {
        return VideoUrlService.BuildSrtPort(opt, index);
    }

    /// <summary>
    /// Builds srt listen url.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="index">The index.</param>
    /// <returns>Result.</returns>
    public static string BuildSrtListenUrl(Options opt, int index)
    {
        return VideoUrlService.BuildSrtListenUrl(opt, index);
    }

    /// <summary>
    /// Builds srt client url.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="index">The index.</param>
    /// <returns>Result.</returns>
    public static string BuildSrtClientUrl(Options opt, int index)
    {
        return VideoUrlService.BuildSrtClientUrl(opt, index);
    }

    /// <summary>
    /// Builds rtmp listen url.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    public static string BuildRtmpListenUrl(Options opt, string id)
    {
        return VideoUrlService.BuildRtmpListenUrl(opt, id);
    }

    /// <summary>
    /// Builds rtmp client url.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    public static string BuildRtmpClientUrl(Options opt, string id)
    {
        return VideoUrlService.BuildRtmpClientUrl(opt, id);
    }

    /// <summary>
    /// Builds hls dir.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    public static string BuildHlsDir(Options opt, string id)
    {
        return VideoUrlService.BuildHlsDir(opt, id);
    }

    /// <summary>
    /// Builds hls url.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    public static string BuildHlsUrl(Options opt, string id)
    {
        return VideoUrlService.BuildHlsUrl(opt, id);
    }
}
