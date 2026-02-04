using HidControl.Application.Abstractions;
using HidControl.Contracts;
using HidControlServer.Services;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts orphan-kill logic to application abstraction.
/// </summary>
public sealed class VideoOrphanKillerAdapter : IVideoOrphanKiller
{
    private readonly Options _options;
    private readonly AppState _appState;

    /// <summary>
    /// Executes VideoOrphanKillerAdapter.
    /// </summary>
    /// <param name="options">Options.</param>
    /// <param name="appState">Application state.</param>
    public VideoOrphanKillerAdapter(Options options, AppState appState)
    {
        _options = options;
        _appState = appState;
    }

    /// <summary>
    /// Kills orphans for a source.
    /// </summary>
    public IReadOnlyList<int> KillOrphans(VideoSourceConfig source)
    {
        _appState.FfmpegProcesses.TryGetValue(source.Id, out var tracked);
        return FfmpegProcessManager.KillFfmpegOrphansForSource(_options, source, tracked);
    }
}
