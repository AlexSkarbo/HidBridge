using HidControl.Application.Abstractions;
using HidControl.Contracts;
using InfraVideoUrlService = HidControl.Infrastructure.Services.VideoUrlService;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts server URL helper to application abstraction.
/// </summary>
public sealed class VideoUrlBuilderAdapter : IVideoUrlBuilder
{
    /// <inheritdoc />
    public string GetVideoBaseUrl(VideoOptions opt) => InfraVideoUrlService.GetVideoBaseUrl(opt);

    /// <inheritdoc />
    public string BuildRtspClientUrl(VideoOptions opt, string id) => InfraVideoUrlService.BuildRtspClientUrl(opt, id);

    /// <inheritdoc />
    public string BuildSrtClientUrl(VideoOptions opt, int index) => InfraVideoUrlService.BuildSrtClientUrl(opt, index);

    /// <inheritdoc />
    public string BuildRtmpClientUrl(VideoOptions opt, string id) => InfraVideoUrlService.BuildRtmpClientUrl(opt, id);

    /// <inheritdoc />
    public string BuildHlsUrl(VideoOptions opt, string id) => InfraVideoUrlService.BuildHlsUrl(opt, id);
}
