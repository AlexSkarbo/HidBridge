using HidControl.Application.Models;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for persisting active video profile config.
/// </summary>
public interface IVideoConfigSaver
{
    /// <summary>
    /// Saves the active profile name into configuration.
    /// </summary>
    /// <param name="activeProfile">Active profile name.</param>
    /// <returns>Save result.</returns>
    ConfigSaveResult SaveActiveProfile(string? activeProfile);
}
