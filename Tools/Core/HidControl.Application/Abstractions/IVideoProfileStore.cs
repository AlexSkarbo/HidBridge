using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for video profile storage.
/// </summary>
public interface IVideoProfileStore
{
    /// <summary>
    /// Gets all profiles.
    /// </summary>
    /// <returns>Profiles list.</returns>
    IReadOnlyList<VideoProfileConfig> GetAll();

    /// <summary>
    /// Gets the active profile name.
    /// </summary>
    string ActiveProfile { get; }

    /// <summary>
    /// Replaces profiles with the provided list.
    /// </summary>
    /// <param name="profiles">Profiles list.</param>
    void ReplaceAll(IReadOnlyList<VideoProfileConfig> profiles);

    /// <summary>
    /// Sets the active profile.
    /// </summary>
    /// <param name="name">Profile name.</param>
    /// <returns>True if the profile exists and was activated.</returns>
    bool SetActive(string name);
}
