namespace HidControl.UseCases.Video;

/// <summary>
/// Use case model for VideoWatchdogOptions.
/// </summary>
/// <param name="FfmpegMaxRestarts">FfmpegMaxRestarts.</param>
/// <param name="FfmpegRestartDelayMs">FfmpegRestartDelayMs.</param>
/// <param name="FfmpegRestartWindowMs">FfmpegRestartWindowMs.</param>
/// <param name="KillOrphanFfmpeg">KillOrphanFfmpeg.</param>
/// <param name="FlvStaleSeconds">FlvStaleSeconds.</param>
public sealed record VideoWatchdogOptions(
    int FfmpegMaxRestarts,
    int FfmpegRestartDelayMs,
    int FfmpegRestartWindowMs,
    bool KillOrphanFfmpeg,
    int FlvStaleSeconds);
