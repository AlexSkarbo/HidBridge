using HidControl.Application.Abstractions;
using HidControl.Application.Models;

namespace HidControl.Application.UseCases;

/// <summary>
/// Stops FFmpeg processes.
/// </summary>
public sealed class StopFfmpegUseCase
{
    private readonly IVideoSourceStore _store;
    private readonly IVideoRuntimeControl _runtime;
    private readonly IVideoProcessTracker _processTracker;

    /// <summary>
    /// Executes StopFfmpegUseCase.
    /// </summary>
    /// <param name="store">Source store.</param>
    /// <param name="runtime">Runtime control.</param>
    /// <param name="processTracker">Process tracker.</param>
    public StopFfmpegUseCase(IVideoSourceStore store, IVideoRuntimeControl runtime, IVideoProcessTracker processTracker)
    {
        _store = store;
        _runtime = runtime;
        _processTracker = processTracker;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    /// <param name="id">Source id.</param>
    /// <returns>Stop result.</returns>
    public FfmpegStopResult Execute(string? id)
    {
        var enabledSources = _store.GetAll().Where(s => s.Enabled).ToList();
        if (string.IsNullOrWhiteSpace(id))
        {
            foreach (var src in enabledSources)
            {
                _runtime.SetManualStop(src.Id, true);
            }
            _runtime.StopFfmpegProcesses();
            return new FfmpegStopResult(true, "stopped_all", null, null);
        }

        foreach (var src in enabledSources.Where(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            _runtime.SetManualStop(src.Id, true);
        }
        if (!_processTracker.IsRunning(id))
        {
            return new FfmpegStopResult(true, "not_running", id, null);
        }
        try
        {
            _runtime.StopFfmpegProcess(id);
            return new FfmpegStopResult(true, "stopped", id, null);
        }
        catch (Exception ex)
        {
            return new FfmpegStopResult(false, "error", id, ex.Message);
        }
    }
}
