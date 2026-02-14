using System.Collections.Concurrent;
using System.Diagnostics;
using HidControl.UseCases;
using HidControl.UseCases.Video;
using HidControlServer.Services;

namespace HidControlServer;

// FFmpeg process orchestration and stream pumping (MJPEG/FLV) from stdout/stderr.
/// <summary>
/// Provides server utility helpers for ffmpegpipeline.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Starts ffmpeg for sources.
    /// </summary>
    public static void StartFfmpegForSources(Options opt,
        VideoProfileStore profiles,
        IReadOnlyList<VideoSourceConfig> sources,
        AppState appState,
        VideoOutputState outputState,
        ConcurrentDictionary<string, Process> ffmpegProcesses,
        ConcurrentDictionary<string, FfmpegProcState> ffmpegStates,
        bool restart,
        List<object> started,
        List<string> errors,
        bool force)
    {
        FfmpegPipelineService.StartFfmpegForSources(
            opt,
            profiles,
            sources,
            appState,
            outputState,
            ffmpegProcesses,
            ffmpegStates,
            restart,
            started,
            errors,
            force);
    }
}
