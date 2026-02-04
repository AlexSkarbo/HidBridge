namespace HidControl.UseCases.Video;

/// <summary>
/// Use case model for VideoRuntimeService.
/// </summary>
public sealed class VideoRuntimeService
{
    private readonly IVideoRuntime _runtime;

    /// <summary>
    /// Executes VideoRuntimeService.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    public VideoRuntimeService(IVideoRuntime runtime)
    {
        _runtime = runtime;
    }

    // Exposes manual-stop state without leaking infrastructure details.
    /// <summary>
    /// Checks whether manual stop.
    /// </summary>
    /// <returns>Result.</returns>
    public bool IsManualStop(string id) => _runtime.IsManualStop(id);

    // Marks a source as manually stopped or allowed for auto-start.
    /// <summary>
    /// Sets manual stop.
    /// </summary>
    /// <param name="id">The id.</param>
    public void SetManualStop(string id, bool stopped) => _runtime.SetManualStop(id, stopped);

    // Stops capture workers for a specific source or all sources.
    /// <summary>
    /// Stops capture workers.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public Task<IReadOnlyList<string>> StopCaptureWorkersAsync(string? id, CancellationToken cancellationToken = default) =>
        _runtime.StopCaptureWorkersAsync(id, cancellationToken);

    // Stops a single FFmpeg process.
    /// <summary>
    /// Stops ffmpeg process.
    /// </summary>
    /// <returns>Result.</returns>
    public bool StopFfmpegProcess(string id) => _runtime.StopFfmpegProcess(id);

    // Stops all FFmpeg processes.
    /// <summary>
    /// Stops ffmpeg processes.
    /// </summary>
    /// <returns>Result.</returns>
    public IReadOnlyList<string> StopFfmpegProcesses() => _runtime.StopFfmpegProcesses();
}
