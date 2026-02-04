using HidControl.UseCases.Video;

namespace HidControl.Application.Models;

/// <summary>
/// Result of applying a video output request (and optional stream restart).
/// </summary>
/// <param name="Ok">True when applied.</param>
/// <param name="State">Current output state (updated when applied).</param>
/// <param name="Error">Error when not applied.</param>
/// <param name="Mode">Resolved output mode.</param>
/// <param name="AnyRunningBefore">True when any FFmpeg process was running before apply.</param>
/// <param name="SourcesEligible">Count of eligible sources for restart.</param>
/// <param name="ManualStops">Count of sources blocked by manual stop.</param>
/// <param name="StoppedWorkers">Capture workers stopped during apply.</param>
/// <param name="StoppedFfmpeg">FFmpeg processes stopped during apply.</param>
/// <param name="StartResult">FFmpeg start result (empty when not started).</param>
public sealed record VideoOutputApplyResult(
    bool Ok,
    VideoOutputState State,
    string? Error,
    string Mode,
    bool AnyRunningBefore,
    int SourcesEligible,
    int ManualStops,
    IReadOnlyList<string> StoppedWorkers,
    IReadOnlyList<string> StoppedFfmpeg,
    FfmpegStartResult StartResult);

