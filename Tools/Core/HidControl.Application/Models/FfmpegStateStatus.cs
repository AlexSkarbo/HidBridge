namespace HidControl.Application.Models;

/// <summary>
/// Snapshot of FFmpeg restart/metadata state for a source.
/// </summary>
/// <param name="Id">Source id.</param>
/// <param name="LastStartAt">Last start timestamp (UTC).</param>
/// <param name="LastExitAt">Last exit timestamp (UTC).</param>
/// <param name="LastExitCode">Last exit code.</param>
/// <param name="RestartCount">Restart counter within the watchdog window.</param>
/// <param name="LogPath">Log file path.</param>
public sealed record FfmpegStateStatus(
    string Id,
    DateTimeOffset? LastStartAt,
    DateTimeOffset? LastExitAt,
    int? LastExitCode,
    int RestartCount,
    string? LogPath);

