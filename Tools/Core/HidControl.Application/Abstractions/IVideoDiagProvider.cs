namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for building video diagnostics payload.
/// </summary>
public interface IVideoDiagProvider
{
    /// <summary>
    /// Builds diagnostics payload.
    /// </summary>
    /// <returns>Diagnostics payload.</returns>
    object Get();
}
