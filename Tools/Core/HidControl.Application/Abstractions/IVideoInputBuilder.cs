using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Builds FFmpeg input arguments for a video source.
/// </summary>
public interface IVideoInputBuilder
{
    /// <summary>
    /// Builds FFmpeg input arguments for a source.
    /// </summary>
    /// <param name="source">Video source.</param>
    /// <returns>Input arguments or null when incompatible.</returns>
    string? BuildFfmpegInput(VideoSourceConfig source);
}

