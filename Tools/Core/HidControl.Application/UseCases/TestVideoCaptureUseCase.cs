using HidControl.Application.Abstractions;
using HidControl.Application.Models;

namespace HidControl.Application.UseCases;

/// <summary>
/// Tests whether a video source can be captured by FFmpeg on the server host.
/// </summary>
public sealed class TestVideoCaptureUseCase
{
    private readonly IVideoTestCaptureProbe _probe;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public TestVideoCaptureUseCase(IVideoTestCaptureProbe probe)
    {
        _probe = probe;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public Task<VideoTestCaptureResult> ExecuteAsync(string? id, int? timeoutSec, CancellationToken ct)
    {
        return _probe.RunAsync(id, timeoutSec, ct);
    }
}

