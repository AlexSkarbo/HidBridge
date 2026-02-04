using System.Collections.Generic;

namespace HidControl.Application.Models;

/// <summary>
/// Result of reading FFmpeg process status.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message.</param>
/// <param name="Processes">Tracked FFmpeg process snapshots.</param>
/// <param name="States">Tracked FFmpeg state snapshots.</param>
public sealed record VideoFfmpegStatusResult(
    bool Ok,
    string? Error,
    IReadOnlyList<FfmpegProcessStatus> Processes,
    IReadOnlyList<FfmpegStateStatus> States);

