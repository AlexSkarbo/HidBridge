using HidControl.UseCases.Video;

namespace HidControlServer.Services;

/// <summary>
/// Bridges watchdog diagnostics to the server event log.
/// </summary>
internal sealed class ServerWatchdogEvents : IVideoWatchdogEvents
{
    /// <inheritdoc />
    public void Log(string category, string message, object? data = null)
    {
        ServerEventLog.Log(category, message, data);
    }
}
