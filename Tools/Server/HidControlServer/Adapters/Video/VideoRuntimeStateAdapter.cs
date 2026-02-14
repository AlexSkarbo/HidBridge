using System.Diagnostics;
using HidControl.Contracts;
using HidControl.Core;
using HidControl.UseCases.Video;
using HidControlServer.Services;

namespace HidControlServer.Adapters.Video;

/// <summary>
/// Adapts VideoRuntimeStateAdapter.
/// </summary>
public sealed class VideoRuntimeStateAdapter : IVideoRuntimeState
{
    private readonly AppState _state;
    private readonly Options _options;

    /// <summary>
    /// Executes VideoRuntimeStateAdapter.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="options">The options.</param>
    public VideoRuntimeStateAdapter(AppState state, Options options)
    {
        _state = state;
        _options = options;
    }

    /// <summary>
    /// Checks whether manual stop.
    /// </summary>
    /// <returns>Result.</returns>
    public bool IsManualStop(string sourceId) => _state.IsManualStop(sourceId);

    /// <summary>
    /// Tries to get ffmpeg process.
    /// </summary>
    /// <param name="sourceId">The sourceId.</param>
    /// <param name="process">The process.</param>
    /// <returns>Result.</returns>
    public bool TryGetFfmpegProcess(string sourceId, out Process process) =>
        _state.FfmpegProcesses.TryGetValue(sourceId, out process!);

    /// <summary>
    /// Tries to remove ffmpeg process.
    /// </summary>
    /// <param name="sourceId">The sourceId.</param>
    /// <param name="process">The process.</param>
    /// <returns>Result.</returns>
    public bool TryRemoveFfmpegProcess(string sourceId, out Process? process) =>
        _state.FfmpegProcesses.TryRemove(sourceId, out process);

    /// <summary>
    /// Gets or add ffmpeg state.
    /// </summary>
    /// <param name="sourceId">The sourceId.</param>
    /// <returns>Result.</returns>
    public FfmpegProcState GetOrAddFfmpegState(string sourceId) =>
        _state.FfmpegStates.GetOrAdd(sourceId, _ => new FfmpegProcState());

    /// <summary>
    /// Gets flv stats.
    /// </summary>
    /// <param name="sourceId">The sourceId.</param>
    /// <returns>Result.</returns>
    public FlvHubStats? GetFlvStats(string sourceId)
    {
        if (!_state.FlvStreamHubs.TryGetValue(sourceId, out var hub))
        {
            return null;
        }
        var stats = hub.GetStats();
        return new FlvHubStats(
            stats.hasHeader,
            stats.hasVideoConfig,
            stats.hasKeyframe,
            stats.subscribersActive,
            stats.lastTagAtUtc);
    }

    /// <summary>
    /// Executes KillOrphansForSource.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="tracked">The tracked.</param>
    public void KillOrphansForSource(VideoSourceConfig source, Process? tracked)
    {
        if (!_options.VideoKillOrphanFfmpeg) return;
        FfmpegProcessManager.KillFfmpegOrphansForSource(_options, source, tracked);
    }
}
