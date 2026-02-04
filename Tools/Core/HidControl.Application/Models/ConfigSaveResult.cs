namespace HidControl.Application.Models;

/// <summary>
/// Result of config save operation.
/// </summary>
/// <param name="Saved">True when config was saved.</param>
/// <param name="Path">Path where config was saved.</param>
/// <param name="Error">Error message when failed.</param>
public sealed record ConfigSaveResult(bool Saved, string? Path, string? Error);
