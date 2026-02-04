using HidControl.Application.Models;
using System.Collections.Generic;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for persisting active video profile config.
/// </summary>
public interface IVideoConfigSaver
{
    /// <summary>
    /// Saves sources into configuration.
    /// </summary>
    /// <param name="sources">Sources.</param>
    /// <returns>Save result.</returns>
    ConfigSaveResult SaveSources(IReadOnlyList<HidControl.Contracts.VideoSourceConfig> sources);

    /// <summary>
    /// Saves the active profile name into configuration.
    /// </summary>
    /// <param name="activeProfile">Active profile name.</param>
    /// <returns>Save result.</returns>
    ConfigSaveResult SaveActiveProfile(string? activeProfile);
}
