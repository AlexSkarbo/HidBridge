namespace HidControl.Application.Models;

/// <summary>
/// Aggregate result for FFmpeg start API.
/// </summary>
/// <param name="Ok">True when start succeeded without errors.</param>
/// <param name="Started">Started process descriptors.</param>
/// <param name="Errors">Errors.</param>
/// <param name="KilledOrphans">Killed orphan processes.</param>
/// <param name="StoppedWorkers">Stopped capture workers.</param>
/// <param name="Error">Top-level error.</param>
/// <param name="Blocked">Blocked sources when manual stop is set.</param>
public sealed record FfmpegStartExecutionResult(
    bool Ok,
    IReadOnlyList<object> Started,
    IReadOnlyList<string> Errors,
    IReadOnlyList<OrphanKillInfo> KilledOrphans,
    IReadOnlyList<string> StoppedWorkers,
    string? Error,
    IReadOnlyList<string>? Blocked);
