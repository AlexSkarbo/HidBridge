using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;
using HidControl.UseCases.Video;
using AppFfmpegStartResult = HidControl.Application.Models.FfmpegStartResult;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts VideoFfmpegService to application abstraction.
/// </summary>
public sealed class VideoFfmpegStarterAdapter : IVideoFfmpegStarter
{
    private readonly VideoFfmpegService _service;

    /// <summary>
    /// Executes VideoFfmpegStarterAdapter.
    /// </summary>
    /// <param name="service">FFmpeg service.</param>
    public VideoFfmpegStarterAdapter(VideoFfmpegService service)
    {
        _service = service;
    }

    /// <summary>
    /// Starts FFmpeg for sources.
    /// </summary>
    public AppFfmpegStartResult StartForSources(IReadOnlyList<VideoSourceConfig> sources, VideoOutputState outputState, bool restart, bool force)
    {
        var result = _service.StartForSources(sources, outputState, restart, force);
        return new AppFfmpegStartResult(result.Started, result.Errors);
    }
}
