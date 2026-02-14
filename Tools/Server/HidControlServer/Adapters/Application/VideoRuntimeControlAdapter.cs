using HidControl.Application.Abstractions;
using HidControl.UseCases.Video;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts VideoRuntimeService to application abstraction.
/// </summary>
public sealed class VideoRuntimeControlAdapter : IVideoRuntimeControl
{
    private readonly VideoRuntimeService _runtime;

    /// <summary>
    /// Executes VideoRuntimeControlAdapter.
    /// </summary>
    /// <param name="runtime">Runtime service.</param>
    public VideoRuntimeControlAdapter(VideoRuntimeService runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// Checks manual stop flag.
    /// </summary>
    public bool IsManualStop(string id) => _runtime.IsManualStop(id);

    /// <summary>
    /// Sets manual stop flag.
    /// </summary>
    public void SetManualStop(string id, bool stopped) => _runtime.SetManualStop(id, stopped);

    /// <summary>
    /// Stops capture workers.
    /// </summary>
    public Task<IReadOnlyList<string>> StopCaptureWorkersAsync(string? id, CancellationToken cancellationToken) =>
        _runtime.StopCaptureWorkersAsync(id, cancellationToken);

    /// <summary>
    /// Stops single FFmpeg process.
    /// </summary>
    public bool StopFfmpegProcess(string id) => _runtime.StopFfmpegProcess(id);

    /// <summary>
    /// Stops all FFmpeg processes.
    /// </summary>
    public IReadOnlyList<string> StopFfmpegProcesses() => _runtime.StopFfmpegProcesses();
}
