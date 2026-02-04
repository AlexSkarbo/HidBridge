using HidControl.Application.Models;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for reading FFmpeg process and state status.
/// </summary>
public interface IVideoFfmpegStatusProvider
{
    /// <summary>
    /// Gets current FFmpeg status snapshot.
    /// </summary>
    VideoFfmpegStatusResult GetStatus();
}

