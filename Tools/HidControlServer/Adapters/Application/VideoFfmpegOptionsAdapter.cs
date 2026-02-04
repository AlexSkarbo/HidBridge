using HidControl.Application.Abstractions;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts options to FFmpeg options abstraction.
/// </summary>
public sealed class VideoFfmpegOptionsAdapter : IVideoFfmpegOptions
{
    private readonly Options _options;

    /// <summary>
    /// Executes VideoFfmpegOptionsAdapter.
    /// </summary>
    /// <param name="options">Options.</param>
    public VideoFfmpegOptionsAdapter(Options options)
    {
        _options = options;
    }

    /// <summary>
    /// Gets orphan-kill flag.
    /// </summary>
    public bool KillOrphansEnabled => _options.VideoKillOrphanFfmpeg;
}
