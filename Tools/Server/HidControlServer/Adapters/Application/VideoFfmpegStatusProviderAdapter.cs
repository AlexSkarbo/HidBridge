using HidControl.Application.Abstractions;
using HidControl.Application.Models;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts server FFmpeg tracking state to application abstraction.
/// </summary>
public sealed class VideoFfmpegStatusProviderAdapter : IVideoFfmpegStatusProvider
{
    private readonly AppState _appState;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public VideoFfmpegStatusProviderAdapter(AppState appState)
    {
        _appState = appState;
    }

    /// <inheritdoc />
    public VideoFfmpegStatusResult GetStatus()
    {
        var processes = new List<FfmpegProcessStatus>(_appState.FfmpegProcesses.Count);
        foreach (var kvp in _appState.FfmpegProcesses)
        {
            var proc = kvp.Value;
            bool exited;
            int? exitCode;
            try
            {
                exited = proc.HasExited;
                exitCode = exited ? proc.ExitCode : null;
            }
            catch
            {
                exited = true;
                exitCode = null;
            }

            processes.Add(new FfmpegProcessStatus(
                Id: kvp.Key,
                Pid: proc.Id,
                Status: exited ? "exited" : "running",
                ExitCode: exitCode));
        }

        var states = new List<FfmpegStateStatus>(_appState.FfmpegStates.Count);
        foreach (var kvp in _appState.FfmpegStates)
        {
            var st = kvp.Value;
            states.Add(new FfmpegStateStatus(
                Id: kvp.Key,
                LastStartAt: st.LastStartAt,
                LastExitAt: st.LastExitAt,
                LastExitCode: st.LastExitCode,
                RestartCount: st.RestartCount,
                LogPath: st.LogPath));
        }

        return new VideoFfmpegStatusResult(true, null, processes, states);
    }
}

