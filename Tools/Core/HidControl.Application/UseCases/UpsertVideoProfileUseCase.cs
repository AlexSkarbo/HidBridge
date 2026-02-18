using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Creates or updates a user video profile.
/// </summary>
public sealed class UpsertVideoProfileUseCase
{
    private readonly IVideoProfileStore _store;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="store">Profile store.</param>
    public UpsertVideoProfileUseCase(IVideoProfileStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Executes create/update.
    /// </summary>
    /// <param name="profile">Profile payload.</param>
    /// <returns>Mutation result with latest snapshot.</returns>
    public ProfileMutationResult Execute(VideoProfileConfig profile)
    {
        bool ok = _store.Upsert(profile, out string? error);
        return new ProfileMutationResult(ok, error, _store.GetAll(), _store.ActiveProfile);
    }
}
