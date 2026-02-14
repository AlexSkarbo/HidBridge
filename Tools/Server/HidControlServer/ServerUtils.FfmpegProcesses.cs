using System.Collections.Concurrent;
using System.Diagnostics;
using HidControlServer.Services;

namespace HidControlServer;

// FFmpeg process discovery/cleanup helpers.
/// <summary>
/// Provides server utility helpers for ffmpegprocesses.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Checks whether device busy.
    /// </summary>
    /// <param name="stderr">The stderr.</param>
    /// <returns>Result.</returns>
    public static bool IsDeviceBusy(string stderr)
    {
        return StreamDiagService.IsDeviceBusy(stderr);
    }

    /// <summary>
    /// Stops ffmpeg processes.
    /// </summary>
    /// <param name="appState">The appState.</param>
    /// <returns>Result.</returns>
    public static List<string> StopFfmpegProcesses(AppState appState)
    {
        return FfmpegProcessManager.StopFfmpegProcesses(appState);
    }

    /// <summary>
    /// Executes KillFfmpegOrphansForSource.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="src">The src.</param>
    /// <param name="tracked">The tracked.</param>
    /// <returns>Result.</returns>
    public static List<int> KillFfmpegOrphansForSource(Options opt, VideoSourceConfig src, Process? tracked)
    {
        return FfmpegProcessManager.KillFfmpegOrphansForSource(opt, src, tracked);
    }

    /// <summary>
    /// Gets ffmpeg orphan infos.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="ffmpegProcesses">The ffmpegProcesses.</param>
    /// <returns>Result.</returns>
    public static List<object> GetFfmpegOrphanInfos(Options opt, IReadOnlyList<VideoSourceConfig> sources, ConcurrentDictionary<string, Process> ffmpegProcesses)
    {
        return FfmpegProcessManager.GetFfmpegOrphanInfos(opt, sources, ffmpegProcesses);
    }
}
