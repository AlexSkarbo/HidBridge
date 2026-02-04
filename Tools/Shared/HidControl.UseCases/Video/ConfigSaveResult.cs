namespace HidControl.UseCases.Video;

/// <summary>
/// Use case model for ConfigSaveResult.
/// </summary>
/// <param name="Saved">Saved.</param>
/// <param name="Path">Path.</param>
/// <param name="Error">Error.</param>
public sealed record ConfigSaveResult(bool Saved, string? Path, string? Error);
