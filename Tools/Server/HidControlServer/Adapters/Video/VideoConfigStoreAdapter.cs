using HidControl.Contracts;
using HidControl.UseCases.Video;
using VideoConfigHelpers = HidControlServer.Services.VideoConfigService;

namespace HidControlServer.Adapters.Video;

/// <summary>
/// Adapts VideoConfigStoreAdapter.
/// </summary>
public sealed class VideoConfigStoreAdapter : IVideoConfigStore
{
    private readonly AppState _appState;

    /// <summary>
    /// Executes VideoConfigStoreAdapter.
    /// </summary>
    /// <param name="appState">The appState.</param>
    public VideoConfigStoreAdapter(AppState appState)
    {
        _appState = appState;
    }

    /// <summary>
    /// Saves sources.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <returns>Result.</returns>
    public ConfigSaveResult SaveSources(IReadOnlyList<VideoSourceConfig> sources)
    {
        bool saved = VideoConfigHelpers.TryPersistVideoSources(_appState, sources, out string? error);
        return new ConfigSaveResult(saved, _appState.ConfigPath, error);
    }

    /// <summary>
    /// Saves active profile.
    /// </summary>
    /// <param name="activeProfile">The activeProfile.</param>
    /// <returns>Result.</returns>
    public ConfigSaveResult SaveActiveProfile(string activeProfile)
    {
        bool saved = VideoConfigHelpers.TryPersistActiveVideoProfile(_appState, activeProfile, out string? error);
        return new ConfigSaveResult(saved, _appState.ConfigPath, error);
    }
}
