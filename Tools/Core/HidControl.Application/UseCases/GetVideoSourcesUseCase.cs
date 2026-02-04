using HidControl.Application.Abstractions;
using HidControl.Contracts;
using System.Collections.Generic;

namespace HidControl.Application.UseCases;

/// <summary>
/// Returns the current list of configured video sources.
/// </summary>
public sealed class GetVideoSourcesUseCase
{
    private readonly IVideoSourceStore _sources;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public GetVideoSourcesUseCase(IVideoSourceStore sources)
    {
        _sources = sources;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public IReadOnlyList<VideoSourceConfig> Execute()
    {
        return _sources.GetAll();
    }
}
