namespace HidControl.Application.Models;

/// <summary>
/// FFmpeg argument string for a video source stream worker.
/// </summary>
/// <param name="Id">Source id.</param>
/// <param name="Args">FFmpeg arguments.</param>
public sealed record VideoStreamArgsInfo(string Id, string Args);

