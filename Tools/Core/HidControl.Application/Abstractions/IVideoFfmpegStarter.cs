using HidControl.Contracts;
using HidControl.UseCases.Video;
using AppFfmpegStartResult = HidControl.Application.Models.FfmpegStartResult;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for starting FFmpeg for video sources.
/// </summary>
public interface IVideoFfmpegStarter
{
    /// <summary>
    /// Starts FFmpeg for sources.
    /// </summary>
    /// <param name="sources">Sources.</param>
    /// <param name="outputState">Output state.</param>
    /// <param name="restart">Restart running processes.</param>
    /// <param name="force">Force start even if already running.</param>
    /// <returns>Start result.</returns>
    AppFfmpegStartResult StartForSources(IReadOnlyList<VideoSourceConfig> sources, VideoOutputState outputState, bool restart, bool force);
}
