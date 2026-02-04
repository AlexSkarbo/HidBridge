using HidControl.Application.Models;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for testing whether a video source can be captured by FFmpeg on the server host.
/// </summary>
public interface IVideoTestCaptureProbe
{
    /// <summary>
    /// Runs a one-second capture probe.
    /// </summary>
    /// <param name="id">Source id or null for default enabled source selection.</param>
    /// <param name="timeoutSec">Timeout seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<VideoTestCaptureResult> RunAsync(string? id, int? timeoutSec, CancellationToken ct);
}

