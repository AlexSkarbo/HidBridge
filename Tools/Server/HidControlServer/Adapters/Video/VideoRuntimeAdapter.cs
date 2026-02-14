using HidControl.UseCases.Video;
using HidControlServer.Services;

namespace HidControlServer.Adapters.Video;

/// <summary>
/// Adapts VideoRuntimeAdapter.
/// </summary>
public sealed class VideoRuntimeAdapter : IVideoRuntime
{
    private readonly AppState _state;
    private static int _debugStopTracesRemaining = 5;

    /// <summary>
    /// Executes VideoRuntimeAdapter.
    /// </summary>
    /// <param name="state">The state.</param>
    public VideoRuntimeAdapter(AppState state)
    {
        _state = state;
    }

    /// <summary>
    /// Checks whether manual stop.
    /// </summary>
    /// <returns>Result.</returns>
    public bool IsManualStop(string id) => _state.IsManualStop(id);

    /// <summary>
    /// Sets manual stop.
    /// </summary>
    /// <param name="id">The id.</param>
    public void SetManualStop(string id, bool stopped) => _state.SetManualStop(id, stopped);

    /// <summary>
    /// Stops capture workers.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="cancellationToken">The cancellationToken.</param>
    /// <returns>Result.</returns>
    public async Task<IReadOnlyList<string>> StopCaptureWorkersAsync(string? id, CancellationToken cancellationToken)
    {
        var stopped = new List<string>();
        foreach (var kvp in _state.VideoCaptureWorkers.ToArray())
        {
            if (!string.IsNullOrWhiteSpace(id) &&
                !string.Equals(kvp.Key, id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_state.VideoCaptureWorkers.TryRemove(kvp.Key, out var worker))
            {
                try
                {
                    await worker.DisposeAsync();
                }
                catch
                {
                    // Best-effort cleanup for worker shutdown.
                }
                stopped.Add(kvp.Key);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return stopped;
    }

    /// <summary>
    /// Stops ffmpeg process.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>Result.</returns>
    public bool StopFfmpegProcess(string id)
    {
        ServerEventLog.Log("ffmpeg", "stop_request", new { id });
        if (System.Threading.Interlocked.Decrement(ref _debugStopTracesRemaining) >= 0)
        {
            var trace = new System.Diagnostics.StackTrace(1, true);
            var frames = trace.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
            var lines = frames.Take(6)
                .Select(f =>
                {
                    var method = f.GetMethod();
                    string name = method is null ? "unknown" : $"{method.DeclaringType?.FullName}.{method.Name}";
                    string file = f.GetFileName() ?? "";
                    int line = f.GetFileLineNumber();
                    return string.IsNullOrWhiteSpace(file) ? name : $"{name} ({System.IO.Path.GetFileName(file)}:{line})";
                })
                .ToArray();
            ServerEventLog.Log("ffmpeg", "stop_request:stack", new { id, stack = lines });
        }
        if (!_state.FfmpegProcesses.TryRemove(id, out var proc))
        {
            ServerEventLog.Log("ffmpeg", "stop_request:missing", new { id });
            return false;
        }

        try { proc.Kill(true); } catch { }
        var now = DateTimeOffset.UtcNow;
        if (_state.FfmpegStates.TryGetValue(id, out var state))
        {
            state.LastExitAt = now;
            state.LastExitCode = proc.HasExited ? proc.ExitCode : null;
            if (state.CaptureLockHeld)
            {
                try { _state.VideoCaptureLock.Release(); } catch { }
                state.CaptureLockHeld = false;
            }
        }
        ServerEventLog.Log("ffmpeg", "stop_request:done", new { id, exitCode = proc.HasExited ? (int?)proc.ExitCode : null });

        return true;
    }

    /// <summary>
    /// Stops ffmpeg processes.
    /// </summary>
    /// <returns>Result.</returns>
    public IReadOnlyList<string> StopFfmpegProcesses() => FfmpegProcessManager.StopFfmpegProcesses(_state);
}
