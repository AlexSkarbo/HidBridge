using HidControl.Application.Abstractions;
using HidControl.Application.Models;

namespace HidControl.Application.UseCases;

/// <summary>
/// Deletes a user video profile.
/// </summary>
public sealed class DeleteVideoProfileUseCase
{
    private readonly IVideoProfileStore _store;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="store">Profile store.</param>
    public DeleteVideoProfileUseCase(IVideoProfileStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Executes delete.
    /// </summary>
    /// <param name="name">Profile name.</param>
    /// <returns>Mutation result with latest snapshot.</returns>
    public ProfileMutationResult Execute(string name)
    {
        bool ok = _store.Delete(name, out string? error);
        return new ProfileMutationResult(ok, error, _store.GetAll(), _store.ActiveProfile);
    }
}
