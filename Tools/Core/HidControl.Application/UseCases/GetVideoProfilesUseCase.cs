using HidControl.Application.Abstractions;
using HidControl.Application.Models;

namespace HidControl.Application.UseCases;

/// <summary>
/// Returns current video profiles and active selection.
/// </summary>
public sealed class GetVideoProfilesUseCase
{
    private readonly IVideoProfileStore _store;

    /// <summary>
    /// Executes GetVideoProfilesUseCase.
    /// </summary>
    /// <param name="store">Profile store.</param>
    public GetVideoProfilesUseCase(IVideoProfileStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    /// <returns>Profiles snapshot.</returns>
    public VideoProfilesSnapshot Execute()
    {
        return new VideoProfilesSnapshot(_store.GetAll(), _store.ActiveProfile);
    }
}
