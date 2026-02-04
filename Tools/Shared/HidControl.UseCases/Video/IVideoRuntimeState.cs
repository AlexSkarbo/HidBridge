using System.Diagnostics;
using HidControl.Contracts;
using HidControl.Core;

namespace HidControl.UseCases.Video;

/// <summary>
/// IVideoRuntimeState.
/// </summary>
public interface IVideoRuntimeState
{
    /// <summary>
    /// Checks whether manual stop.
    /// </summary>
    /// <param name="sourceId">The sourceId.</param>
    /// <returns>Result.</returns>
    bool IsManualStop(string sourceId);
    /// <summary>
    /// Tries to get ffmpeg process.
    /// </summary>
    /// <param name="sourceId">The sourceId.</param>
    /// <param name="process">The process.</param>
    /// <returns>Result.</returns>
    bool TryGetFfmpegProcess(string sourceId, out Process process);
    /// <summary>
    /// Tries to remove ffmpeg process.
    /// </summary>
    /// <param name="sourceId">The sourceId.</param>
    /// <param name="process">The process.</param>
    /// <returns>Result.</returns>
    bool TryRemoveFfmpegProcess(string sourceId, out Process? process);
    /// <summary>
    /// Gets or add ffmpeg state.
    /// </summary>
    /// <param name="sourceId">The sourceId.</param>
    /// <returns>Result.</returns>
    FfmpegProcState GetOrAddFfmpegState(string sourceId);
    /// <summary>
    /// Gets flv stats.
    /// </summary>
    /// <param name="sourceId">The sourceId.</param>
    /// <returns>Result.</returns>
    FlvHubStats? GetFlvStats(string sourceId);
    /// <summary>
    /// Executes KillOrphansForSource.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="tracked">The tracked.</param>
    void KillOrphansForSource(VideoSourceConfig source, Process? tracked);
}
