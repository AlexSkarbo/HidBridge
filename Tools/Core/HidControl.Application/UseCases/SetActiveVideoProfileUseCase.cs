using HidControl.Application.Abstractions;
using HidControl.Application.Models;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sets the active video profile and persists config.
/// </summary>
public sealed class SetActiveVideoProfileUseCase
{
    private readonly IVideoProfileStore _store;
    private readonly IVideoConfigSaver _configSaver;

    /// <summary>
    /// Executes SetActiveVideoProfileUseCase.
    /// </summary>
    /// <param name="store">Profile store.</param>
    /// <param name="configSaver">Config saver.</param>
    public SetActiveVideoProfileUseCase(IVideoProfileStore store, IVideoConfigSaver configSaver)
    {
        _store = store;
        _configSaver = configSaver;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    /// <param name="name">Profile name.</param>
    /// <returns>Change result.</returns>
    public ActiveProfileChangeResult Execute(string name)
    {
        bool ok = _store.SetActive(name);
        bool configSaved = false;
        string? configPath = null;
        string? configError = null;
        if (ok)
        {
            var save = _configSaver.SaveActiveProfile(_store.ActiveProfile);
            configSaved = save.Saved;
            configPath = save.Path;
            configError = save.Error;
        }
        return new ActiveProfileChangeResult(ok, _store.ActiveProfile, configSaved, configPath, configError);
    }
}
