using HidControl.Application.Abstractions;
using HidControlServer.Services;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts server-side FFmpeg input builder to application abstraction.
/// </summary>
public sealed class VideoInputBuilderAdapter : IVideoInputBuilder
{
    /// <inheritdoc />
    public string? BuildFfmpegInput(VideoSourceConfig source) => VideoInputService.BuildFfmpegInput(source);
}

