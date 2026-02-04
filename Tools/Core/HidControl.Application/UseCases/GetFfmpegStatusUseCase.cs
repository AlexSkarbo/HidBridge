using HidControl.Application.Abstractions;
using HidControl.Application.Models;

namespace HidControl.Application.UseCases;

/// <summary>
/// Returns the current FFmpeg process and state status.
/// </summary>
public sealed class GetFfmpegStatusUseCase
{
    private readonly IVideoFfmpegStatusProvider _status;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public GetFfmpegStatusUseCase(IVideoFfmpegStatusProvider status)
    {
        _status = status;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public VideoFfmpegStatusResult Execute()
    {
        return _status.GetStatus();
    }
}

