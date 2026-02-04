using HidControl.Contracts;

namespace HidControl.UseCases.Video;

/// <summary>
/// IVideoConfigStore.
/// </summary>
public interface IVideoConfigStore
{
    /// <summary>
    /// Saves sources.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <returns>Result.</returns>
    ConfigSaveResult SaveSources(IReadOnlyList<VideoSourceConfig> sources);
    /// <summary>
    /// Saves active profile.
    /// </summary>
    /// <param name="activeProfile">The activeProfile.</param>
    /// <returns>Result.</returns>
    ConfigSaveResult SaveActiveProfile(string activeProfile);
}
