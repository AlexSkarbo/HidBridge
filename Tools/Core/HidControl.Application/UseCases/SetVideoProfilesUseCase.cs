using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Replaces video profiles and optionally switches active profile.
/// </summary>
public sealed class SetVideoProfilesUseCase
{
    private readonly IVideoProfileStore _store;

    /// <summary>
    /// Executes SetVideoProfilesUseCase.
    /// </summary>
    /// <param name="store">Profile store.</param>
    public SetVideoProfilesUseCase(IVideoProfileStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    /// <param name="profiles">Profiles list.</param>
    /// <param name="active">Optional active profile name.</param>
    /// <returns>Profiles snapshot.</returns>
    public VideoProfilesSnapshot Execute(IReadOnlyList<VideoProfileConfig>? profiles, string? active)
    {
        _store.ReplaceAll(profiles ?? Array.Empty<VideoProfileConfig>());
        if (!string.IsNullOrWhiteSpace(active))
        {
            _store.SetActive(active);
        }
        return new VideoProfilesSnapshot(_store.GetAll(), _store.ActiveProfile);
    }
}
