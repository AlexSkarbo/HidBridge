using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;
using System.Collections.Generic;

namespace HidControl.Application.UseCases;

/// <summary>
/// Replaces the full list of video sources and persists them to config.
/// </summary>
public sealed class SetVideoSourcesUseCase
{
    private readonly IVideoSourceStore _sources;
    private readonly IVideoConfigSaver _configSaver;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public SetVideoSourcesUseCase(IVideoSourceStore sources, IVideoConfigSaver configSaver)
    {
        _sources = sources;
        _configSaver = configSaver;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public VideoSourcesSaveResult Execute(VideoSourcesRequest req)
    {
        var sources = req.Sources ?? new List<VideoSourceConfig>();
        _sources.ReplaceAll(sources);

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
