using HidControl.Contracts;
using InfraVideoUrlService = HidControl.Infrastructure.Services.VideoUrlService;

namespace HidControlServer.Services;

/// <summary>
/// Builds bind and stream URLs for video endpoints.
/// </summary>
internal static class VideoUrlService
{
    /// <summary>
    /// Builds server bind URLs from options.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <returns>Bind URLs.</returns>
    public static string[] BuildBindUrls(Options opt) =>
        InfraVideoUrlService.BuildBindUrls(opt.ToVideoPipelineOptions().ToVideoOptions());

    /// <summary>
    /// Builds server bind URLs from pipeline options.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <returns>Bind URLs.</returns>
    public static string[] BuildBindUrls(VideoPipelineOptions opt) =>
        InfraVideoUrlService.BuildBindUrls(opt.ToVideoOptions());

    /// <summary>
    /// Gets the host for video endpoints based on options.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <returns>Host name.</returns>
    public static string GetVideoHost(Options opt) =>
        InfraVideoUrlService.GetVideoHost(opt.ToVideoPipelineOptions().ToVideoOptions());

    /// <summary>
    /// Gets the host for video endpoints based on pipeline options.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <returns>Host name.</returns>
    public static string GetVideoHost(VideoPipelineOptions opt) =>
        InfraVideoUrlService.GetVideoHost(opt.ToVideoOptions());

    /// <summary>
    /// Gets the base URL for video endpoints.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <returns>Base URL.</returns>
    public static string GetVideoBaseUrl(Options opt) =>
        InfraVideoUrlService.GetVideoBaseUrl(opt.ToVideoPipelineOptions().ToVideoOptions());

    /// <summary>
    /// Gets the base URL for video endpoints.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <returns>Base URL.</returns>
    public static string GetVideoBaseUrl(VideoPipelineOptions opt) =>
        InfraVideoUrlService.GetVideoBaseUrl(opt.ToVideoOptions());

    /// <summary>
    /// Builds an RTSP listen URL.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Listen URL.</returns>
    public static string BuildRtspListenUrl(Options opt, string id) =>
        InfraVideoUrlService.BuildRtspListenUrl(opt.ToVideoPipelineOptions().ToVideoOptions(), id);

    /// <summary>
    /// Builds an RTSP listen URL.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Listen URL.</returns>
    public static string BuildRtspListenUrl(VideoPipelineOptions opt, string id) =>
        InfraVideoUrlService.BuildRtspListenUrl(opt.ToVideoOptions(), id);

    /// <summary>
    /// Builds an RTSP client URL.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Client URL.</returns>
    public static string BuildRtspClientUrl(Options opt, string id) =>
        InfraVideoUrlService.BuildRtspClientUrl(opt.ToVideoPipelineOptions().ToVideoOptions(), id);

    /// <summary>
    /// Builds an RTSP client URL.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Client URL.</returns>
    public static string BuildRtspClientUrl(VideoPipelineOptions opt, string id) =>
        InfraVideoUrlService.BuildRtspClientUrl(opt.ToVideoOptions(), id);

    /// <summary>
    /// Builds an SRT port for a source index.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="index">Source index.</param>
    /// <returns>Port number.</returns>
    public static int BuildSrtPort(Options opt, int index) =>
        InfraVideoUrlService.BuildSrtPort(opt.ToVideoPipelineOptions().ToVideoOptions(), index);

    /// <summary>
    /// Builds an SRT port for a source index.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <param name="index">Source index.</param>
    /// <returns>Port number.</returns>
    public static int BuildSrtPort(VideoPipelineOptions opt, int index) =>
        InfraVideoUrlService.BuildSrtPort(opt.ToVideoOptions(), index);

    /// <summary>
    /// Builds an SRT listen URL.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="index">Source index.</param>
    /// <returns>Listen URL.</returns>
    public static string BuildSrtListenUrl(Options opt, int index) =>
        InfraVideoUrlService.BuildSrtListenUrl(opt.ToVideoPipelineOptions().ToVideoOptions(), index);

    /// <summary>
    /// Builds an SRT listen URL.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <param name="index">Source index.</param>
    /// <returns>Listen URL.</returns>
    public static string BuildSrtListenUrl(VideoPipelineOptions opt, int index) =>
        InfraVideoUrlService.BuildSrtListenUrl(opt.ToVideoOptions(), index);

    /// <summary>
    /// Builds an SRT client URL.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="index">Source index.</param>
    /// <returns>Client URL.</returns>
    public static string BuildSrtClientUrl(Options opt, int index) =>
        InfraVideoUrlService.BuildSrtClientUrl(opt.ToVideoPipelineOptions().ToVideoOptions(), index);

    /// <summary>
    /// Builds an SRT client URL.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <param name="index">Source index.</param>
    /// <returns>Client URL.</returns>
    public static string BuildSrtClientUrl(VideoPipelineOptions opt, int index) =>
        InfraVideoUrlService.BuildSrtClientUrl(opt.ToVideoOptions(), index);

    /// <summary>
    /// Builds an RTMP listen URL.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Listen URL.</returns>
    public static string BuildRtmpListenUrl(Options opt, string id) =>
        InfraVideoUrlService.BuildRtmpListenUrl(opt.ToVideoPipelineOptions().ToVideoOptions(), id);

    /// <summary>
    /// Builds an RTMP listen URL.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Listen URL.</returns>
    public static string BuildRtmpListenUrl(VideoPipelineOptions opt, string id) =>
        InfraVideoUrlService.BuildRtmpListenUrl(opt.ToVideoOptions(), id);

    /// <summary>
    /// Builds an RTMP client URL.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Client URL.</returns>
    public static string BuildRtmpClientUrl(Options opt, string id) =>
        InfraVideoUrlService.BuildRtmpClientUrl(opt.ToVideoPipelineOptions().ToVideoOptions(), id);

    /// <summary>
    /// Builds an RTMP client URL.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Client URL.</returns>
    public static string BuildRtmpClientUrl(VideoPipelineOptions opt, string id) =>
        InfraVideoUrlService.BuildRtmpClientUrl(opt.ToVideoOptions(), id);

    /// <summary>
    /// Builds a local HLS output directory.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Directory path.</returns>
    public static string BuildHlsDir(Options opt, string id) =>
        InfraVideoUrlService.BuildHlsDir(opt.ToVideoPipelineOptions().ToVideoOptions(), id);

    /// <summary>
    /// Builds a local HLS output directory.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>Directory path.</returns>
    public static string BuildHlsDir(VideoPipelineOptions opt, string id) =>
        InfraVideoUrlService.BuildHlsDir(opt.ToVideoOptions(), id);

    /// <summary>
    /// Builds an HLS playback URL.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>HLS URL.</returns>
    public static string BuildHlsUrl(Options opt, string id) =>
        InfraVideoUrlService.BuildHlsUrl(opt.ToVideoPipelineOptions().ToVideoOptions(), id);

    /// <summary>
    /// Builds an HLS playback URL.
    /// </summary>
    /// <param name="opt">Pipeline options.</param>
    /// <param name="id">Source ID.</param>
    /// <returns>HLS URL.</returns>
    public static string BuildHlsUrl(VideoPipelineOptions opt, string id) =>
        InfraVideoUrlService.BuildHlsUrl(opt.ToVideoOptions(), id);
}

