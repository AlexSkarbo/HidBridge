using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Inserts or updates a single video source and persists it to config.
/// </summary>
public sealed class UpsertVideoSourceUseCase
{
    private readonly IVideoSourceStore _sources;
    private readonly IVideoConfigSaver _configSaver;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public UpsertVideoSourceUseCase(IVideoSourceStore sources, IVideoConfigSaver configSaver)
    {
        _sources = sources;
        _configSaver = configSaver;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public VideoSourcesSaveResult Execute(VideoSourceConfig source)
    {
        _sources.Upsert(source);
        var save = _configSaver.SaveSources(_sources.GetAll());
        return new VideoSourcesSaveResult(
            Ok: true,
            Error: null,
            Sources: _sources.GetAll(),
            ConfigSaved: save.Saved,
            ConfigPath: save.Path,
            ConfigError: save.Error);
    }
}

