using HidControl.Application.Abstractions;
using HidControl.Contracts;
using HidControl.UseCases;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts VideoProfileStore to application abstraction.
/// </summary>
public sealed class VideoProfileStoreAdapter : IVideoProfileStore
{
    private readonly VideoProfileStore _store;

    /// <summary>
    /// Executes VideoProfileStoreAdapter.
    /// </summary>
    /// <param name="store">Store.</param>
    public VideoProfileStoreAdapter(VideoProfileStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Gets the active profile.
    /// </summary>
    public string ActiveProfile => _store.ActiveProfile;

    /// <summary>
    /// Gets all profiles.
    /// </summary>
    /// <returns>Profiles.</returns>
    public IReadOnlyList<VideoProfileConfig> GetAll() => _store.GetAll();

    /// <summary>
    /// Replaces all profiles.
    /// </summary>
    /// <param name="profiles">Profiles.</param>
    public void ReplaceAll(IReadOnlyList<VideoProfileConfig> profiles)
    {
        _store.ReplaceAll(profiles);
    }

    /// <summary>
    /// Sets active profile.
    /// </summary>
    /// <param name="name">Profile name.</param>
    /// <returns>True when activated.</returns>
    public bool SetActive(string name) => _store.SetActive(name);
}
