using HidControl.Application.Abstractions;
using HidControl.Contracts;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts VideoProfileStore to application abstraction.
/// </summary>
public sealed class VideoProfileStoreAdapter : IVideoProfileStore
{
    private readonly VideoProfileStore _store;
    private readonly IHidStateStore? _stateStore;

    /// <summary>
    /// Executes VideoProfileStoreAdapter.
    /// </summary>
    /// <param name="store">Store.</param>
    public VideoProfileStoreAdapter(VideoProfileStore store, IHidStateStore? stateStore = null)
    {
        _store = store;
        _stateStore = stateStore;
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
        PersistProfilesSnapshot();
    }

    /// <summary>
    /// Sets active profile.
    /// </summary>
    /// <param name="name">Profile name.</param>
    /// <returns>True when activated.</returns>
    public bool SetActive(string name)
    {
        bool ok = _store.SetActive(name);
        if (ok)
        {
            PersistProfilesSnapshot();
        }
        return ok;
    }

    /// <inheritdoc />
    public bool Upsert(VideoProfileConfig profile, out string? error)
    {
        bool ok = _store.Upsert(profile, out error);
        if (ok)
        {
            PersistProfilesSnapshot();
        }
        return ok;
    }

    /// <inheritdoc />
    public bool Delete(string name, out string? error)
    {
        bool ok = _store.Delete(name, out error);
        if (ok)
        {
            PersistProfilesSnapshot();
        }
        return ok;
    }

    private void PersistProfilesSnapshot()
    {
        _stateStore?.SaveVideoProfiles(_store.GetAll(), _store.ActiveProfile);
    }
}
