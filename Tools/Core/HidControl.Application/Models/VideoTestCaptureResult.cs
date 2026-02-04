using System;

namespace HidControl.Application.Models;

/// <summary>
/// Result of running a one-second FFmpeg capture probe for a source.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Source">Source id used for the probe.</param>
/// <param name="Skipped">Skip reason (e.g. worker_running, ffmpeg_running).</param>
/// <param name="WorkerLatestFrameAt">Latest frame timestamp when skipped due to a running worker.</param>
/// <param name="Pid">Running FFmpeg PID when skipped due to a running process.</param>
/// <param name="ExitCode">FFmpeg exit code.</param>
/// <param name="Args">FFmpeg arguments used for the probe.</param>
/// <param name="Stderr">Tail of stderr output.</param>
/// <param name="Error">Error message/code when <paramref name="Ok"/> is false.</param>
public sealed record VideoTestCaptureResult(
    bool Ok,
    string? Source,
    string? Skipped,
    DateTimeOffset? WorkerLatestFrameAt,
    int? Pid,
    int? ExitCode,
    string? Args,
    string? Stderr,
    string? Error);

