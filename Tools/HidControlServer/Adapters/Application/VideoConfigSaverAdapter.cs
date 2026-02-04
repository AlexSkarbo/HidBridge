using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.UseCases.Video;
using AppConfigSaveResult = HidControl.Application.Models.ConfigSaveResult;
using System.Collections.Generic;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts VideoConfigService to application abstraction.
/// </summary>
public sealed class VideoConfigSaverAdapter : IVideoConfigSaver
{
    private readonly VideoConfigService _service;

    /// <summary>
    /// Executes VideoConfigSaverAdapter.
    /// </summary>
    /// <param name="service">Config service.</param>
    public VideoConfigSaverAdapter(VideoConfigService service)
    {
        _service = service;
    }

    /// <summary>
    /// Saves active profile to config.
    /// </summary>
    /// <param name="activeProfile">Active profile.</param>
    /// <returns>Save result.</returns>
    public AppConfigSaveResult SaveActiveProfile(string? activeProfile)
    {
        if (string.IsNullOrWhiteSpace(activeProfile))
        {
            return new AppConfigSaveResult(false, null, "active profile is required");
        }
        var save = _service.SaveActiveProfile(activeProfile);
        return new AppConfigSaveResult(save.Saved, save.Path, save.Error);
    }

    /// <summary>
    /// Saves sources to config.
    /// </summary>
    /// <param name="sources">Sources.</param>
    /// <returns>Save result.</returns>
    public AppConfigSaveResult SaveSources(IReadOnlyList<HidControl.Contracts.VideoSourceConfig> sources)
    {
        var save = _service.SaveSources(sources);
        return new AppConfigSaveResult(save.Saved, save.Path, save.Error);
    }
}
