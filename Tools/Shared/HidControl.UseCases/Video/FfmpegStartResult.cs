namespace HidControl.UseCases.Video;

/// <summary>
/// Use case model for FfmpegStartResult.
/// </summary>
/// <param name="Started">Started.</param>
/// <param name="Errors">Errors.</param>
public sealed record FfmpegStartResult(IReadOnlyList<object> Started, IReadOnlyList<string> Errors);
