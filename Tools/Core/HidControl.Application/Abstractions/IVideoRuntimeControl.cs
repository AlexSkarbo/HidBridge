namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for runtime control of capture and FFmpeg processes.
/// </summary>
public interface IVideoRuntimeControl
{
    /// <summary>
    /// Checks whether a source is marked as manual stop.
    /// </summary>
    /// <param name="id">Source id.</param>
    /// <returns>True if manual stop is set.</returns>
    bool IsManualStop(string id);

    /// <summary>
    /// Sets manual stop flag for a source.
    /// </summary>
    /// <param name="id">Source id.</param>
    /// <param name="stopped">Manual stop flag.</param>
    void SetManualStop(string id, bool stopped);

    /// <summary>
    /// Stops capture workers.
    /// </summary>
    /// <param name="id">Source id or null for all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stopped worker ids.</returns>
    Task<IReadOnlyList<string>> StopCaptureWorkersAsync(string? id, CancellationToken cancellationToken);

    /// <summary>
    /// Stops a single FFmpeg process.
    /// </summary>
    /// <param name="id">Source id.</param>
    /// <returns>True if stopped.</returns>
    bool StopFfmpegProcess(string id);

    /// <summary>
    /// Stops all FFmpeg processes.
    /// </summary>
    /// <returns>Stopped source ids.</returns>
    IReadOnlyList<string> StopFfmpegProcesses();
}
