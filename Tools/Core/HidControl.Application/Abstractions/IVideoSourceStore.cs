using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for video source storage.
/// </summary>
public interface IVideoSourceStore
{
    /// <summary>
    /// Gets all sources.
    /// </summary>
    /// <returns>Sources list.</returns>
    IReadOnlyList<VideoSourceConfig> GetAll();
}
