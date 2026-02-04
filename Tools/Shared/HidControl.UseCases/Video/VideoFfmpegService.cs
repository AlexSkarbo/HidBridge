using HidControl.Contracts;

namespace HidControl.UseCases.Video;

/// <summary>
/// Use case model for VideoFfmpegService.
/// </summary>
public sealed class VideoFfmpegService
{
    private readonly IVideoFfmpegController _controller;

    /// <summary>
    /// Executes VideoFfmpegService.
    /// </summary>
    /// <param name="controller">The controller.</param>
    public VideoFfmpegService(IVideoFfmpegController controller)
    {
        _controller = controller;
    }

    /// <summary>
    /// Starts for sources.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <param name="outputState">The outputState.</param>
    /// <param name="restart">The restart.</param>
    /// <param name="force">The force.</param>
    /// <returns>Result.</returns>
    public FfmpegStartResult StartForSources(IReadOnlyList<VideoSourceConfig> sources, VideoOutputState outputState, bool restart, bool force)
    {
        return _controller.StartForSources(sources, outputState, restart, force);
    }
}
