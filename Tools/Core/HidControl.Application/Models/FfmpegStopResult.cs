namespace HidControl.Application.Models;

/// <summary>
/// Result for FFmpeg stop API.
/// </summary>
/// <param name="Ok">True when stop succeeded.</param>
/// <param name="Status">Status string.</param>
/// <param name="Id">Source id.</param>
/// <param name="Error">Error message.</param>
public sealed record FfmpegStopResult(bool Ok, string Status, string? Id, string? Error);
