namespace HidControlServer.Services;

/// <summary>
/// Persists and indexes video sources and profiles in config.
/// </summary>
internal static class VideoConfigService
{
    /// <summary>
    /// Finds the index of a source in a list by ID.
    /// </summary>
    /// <param name="sources">Video sources.</param>
    /// <param name="src">Source.</param>
    /// <returns>Index of the source or 0.</returns>
    public static int GetVideoSourceIndex(IReadOnlyList<VideoSourceConfig> sources, VideoSourceConfig src)
    {
        for (int i = 0; i < sources.Count; i++)
        {
            if (string.Equals(sources[i].Id, src.Id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }

    /// <summary>
    /// Persists the video sources to the config file.
    /// </summary>
    /// <param name="appState">Application state.</param>
    /// <param name="sources">Video sources.</param>
    /// <param name="error">Error message.</param>
    /// <returns>True if saved.</returns>
    public static bool TryPersistVideoSources(AppState appState, IReadOnlyList<VideoSourceConfig> sources, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(appState.ConfigPath))
        {
            error = "config_path_unresolved";
            return false;
        }
        return Options.TryUpdateConfigVideoSources(appState.ConfigPath, sources, out error);
    }

    /// <summary>
    /// Persists the active video profile name to the config file.
    /// </summary>
    /// <param name="appState">Application state.</param>
    /// <param name="activeProfile">Active profile name.</param>
    /// <param name="error">Error message.</param>
    /// <returns>True if saved.</returns>
    public static bool TryPersistActiveVideoProfile(AppState appState, string activeProfile, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(appState.ConfigPath))
        {
            error = "config_path_unresolved";
            return false;
        }
        return Options.TryUpdateConfigActiveVideoProfile(appState.ConfigPath, activeProfile, out error);
    }
}
