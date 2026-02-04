namespace HidControl.UseCases.Video;

/// <summary>
/// IVideoRuntime.
/// </summary>
public interface IVideoRuntime
{
    // Tracks user-initiated stops that block auto-start logic.
    /// <summary>
    /// Checks whether manual stop.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    bool IsManualStop(string id);
    // Updates the manual-stop flag for a source.
    /// <summary>
    /// Sets manual stop.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="stopped">The stopped.</param>
    void SetManualStop(string id, bool stopped);
    // Stops capture workers and returns the IDs that were stopped.
    /// <summary>
    /// Stops capture workers.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    Task<IReadOnlyList<string>> StopCaptureWorkersAsync(string? id, CancellationToken cancellationToken);
    // Stops a single FFmpeg process by source ID.
    /// <summary>
    /// Stops ffmpeg process.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    bool StopFfmpegProcess(string id);
    // Stops all FFmpeg processes and returns the stopped IDs.
    /// <summary>
    /// Stops ffmpeg processes.
    /// </summary>
    /// <returns>Result.</returns>
    IReadOnlyList<string> StopFfmpegProcesses();
}
