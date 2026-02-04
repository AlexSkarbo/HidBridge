namespace HidControl.Application.Abstractions;

/// <summary>
/// FFmpeg-related options for application use cases.
/// </summary>
public interface IVideoFfmpegOptions
{
    /// <summary>
    /// True when orphan killing is enabled.
    /// </summary>
    bool KillOrphansEnabled { get; }
}
