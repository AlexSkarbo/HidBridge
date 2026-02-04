using HidControl.Application.Abstractions;
using System.Linq;
using System.Collections.Generic;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts process tracking to application abstraction.
/// </summary>
public sealed class VideoProcessTrackerAdapter : IVideoProcessTracker
{
    private readonly AppState _appState;

    /// <summary>
    /// Executes VideoProcessTrackerAdapter.
    /// </summary>
    /// <param name="appState">Application state.</param>
    public VideoProcessTrackerAdapter(AppState appState)
    {
        _appState = appState;
    }

    /// <summary>
    /// Checks if any process is running.
    /// </summary>
    public bool AnyRunning()
    {
        return _appState.FfmpegProcesses.Values.Any(proc => proc is not null && !proc.HasExited);
    }

    /// <summary>
    /// Gets tracked process ids.
    /// </summary>
    public IReadOnlyList<string> GetTrackedIds()
    {
        return _appState.FfmpegProcesses.Keys.ToArray();
    }

    /// <summary>
    /// Checks if a process is running.
    /// </summary>
    public bool IsRunning(string id)
    {
        if (!_appState.FfmpegProcesses.TryGetValue(id, out var proc))
        {
            return false;
        }
        return !proc.HasExited;
    }
}
