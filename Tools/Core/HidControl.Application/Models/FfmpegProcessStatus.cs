namespace HidControl.Application.Models;

/// <summary>
/// Snapshot of a tracked FFmpeg process.
/// </summary>
/// <param name="Id">Source id.</param>
/// <param name="Pid">Process id.</param>
/// <param name="Status">Status string: running/exited.</param>
/// <param name="ExitCode">Exit code when exited.</param>
public sealed record FfmpegProcessStatus(string Id, int Pid, string Status, int? ExitCode);

