namespace HidControl.Application.Models;

/// <summary>
/// Result of FFmpeg start operation.
/// </summary>
/// <param name="Started">Started process descriptors.</param>
/// <param name="Errors">Errors.</param>
public sealed record FfmpegStartResult(IReadOnlyList<object> Started, IReadOnlyList<string> Errors);
