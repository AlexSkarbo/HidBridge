namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for tracking running FFmpeg processes.
/// </summary>
public interface IVideoProcessTracker
{
    /// <summary>
    /// Checks if any FFmpeg process is currently running.
    /// </summary>
    /// <returns>True when any is running.</returns>
    bool AnyRunning();

    /// <summary>
    /// Gets tracked FFmpeg process source ids.
    /// </summary>
    /// <returns>Source ids.</returns>
    IReadOnlyList<string> GetTrackedIds();

    /// <summary>
    /// Checks if a process is running for a source.
    /// </summary>
    /// <param name="id">Source id.</param>
    /// <returns>True when running.</returns>
    bool IsRunning(string id);
}
