namespace HidControl.UseCases.Video;

/// <summary>
/// Emits watchdog diagnostics for operational visibility.
/// </summary>
public interface IVideoWatchdogEvents
{
    /// <summary>
    /// Writes a watchdog event entry.
    /// </summary>
    /// <param name="category">Event category.</param>
    /// <param name="message">Event message.</param>
    /// <param name="data">Optional structured payload.</param>
    void Log(string category, string message, object? data = null);
}
