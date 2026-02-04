using HidControl.Contracts;

namespace HidControl.UseCases.Video;

/// <summary>
/// IVideoFfmpegController.
/// </summary>
public interface IVideoFfmpegController
{
    /// <summary>
    /// Starts for sources.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <param name="outputState">The outputState.</param>
    /// <param name="restart">The restart.</param>
    /// <param name="force">The force.</param>
    /// <returns>Result.</returns>
    FfmpegStartResult StartForSources(IReadOnlyList<VideoSourceConfig> sources, VideoOutputState outputState, bool restart, bool force);
}
