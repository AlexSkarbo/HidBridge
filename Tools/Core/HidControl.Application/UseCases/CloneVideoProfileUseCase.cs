using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Clones an existing profile into a new user profile.
/// </summary>
public sealed class CloneVideoProfileUseCase
{
    private readonly IVideoProfileStore _store;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="store">Profile store.</param>
    public CloneVideoProfileUseCase(IVideoProfileStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Executes clone operation.
    /// </summary>
    /// <param name="source">Source profile name.</param>
    /// <param name="target">Target profile name.</param>
    /// <returns>Mutation result with latest snapshot.</returns>
    public ProfileMutationResult Execute(string? source, string? target)
    {
        string src = source?.Trim() ?? string.Empty;
        string dst = target?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
        {
            return new ProfileMutationResult(false, "name_required", _store.GetAll(), _store.ActiveProfile);
        }

        var all = _store.GetAll();
        VideoProfileConfig? from = all.FirstOrDefault(p => string.Equals(p.Name, src, StringComparison.OrdinalIgnoreCase));
        if (from is null)
        {
            return new ProfileMutationResult(false, "profile_not_found", all, _store.ActiveProfile);
        }

        if (all.Any(p => string.Equals(p.Name, dst, StringComparison.OrdinalIgnoreCase)))
        {
            return new ProfileMutationResult(false, "profile_exists", all, _store.ActiveProfile);
        }

        var copy = new VideoProfileConfig(
            dst,
            from.Args,
            from.Note,
            from.AudioEnabled,
            from.AudioInput,
            from.AudioBitrateKbps,
            IsReadonly: false);

        bool ok = _store.Upsert(copy, out string? error);
        return new ProfileMutationResult(ok, error, _store.GetAll(), _store.ActiveProfile);
    }
}
