using HidControl.Contracts;

namespace HidControl.UseCases.Video;

/// <summary>
/// Use case model for VideoConfigService.
/// </summary>
public sealed class VideoConfigService
{
    private readonly IVideoConfigStore _store;

    /// <summary>
    /// Executes VideoConfigService.
    /// </summary>
    /// <param name="store">The store.</param>
    public VideoConfigService(IVideoConfigStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Saves sources.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <returns>Result.</returns>
    public ConfigSaveResult SaveSources(IReadOnlyList<VideoSourceConfig> sources)
    {
        return _store.SaveSources(sources);
    }

    /// <summary>
    /// Saves active profile.
    /// </summary>
    /// <param name="activeProfile">The activeProfile.</param>
    /// <returns>Result.</returns>
    public ConfigSaveResult SaveActiveProfile(string activeProfile)
    {
        return _store.SaveActiveProfile(activeProfile);
    }
}
