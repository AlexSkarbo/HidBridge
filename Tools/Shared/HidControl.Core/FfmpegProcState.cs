using System.Diagnostics;

namespace HidControl.Core;

// Mutable process metadata used by the capture supervisor.
/// <summary>
/// Core model for FfmpegProcState.
/// </summary>
public sealed class FfmpegProcState
{
    public Process? Process;
    public DateTimeOffset? LastStartAt;
    public DateTimeOffset? LastExitAt;
    public int? LastExitCode;
    public int RestartCount;
    public DateTimeOffset? WindowStart;
    public string? LogPath;
    public string? Args;
    public bool CaptureLockHeld;
    public DateTimeOffset? CaptureLockAt;
}
