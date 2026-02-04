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

    /// <summary>
    /// Replaces all sources.
    /// </summary>
    /// <param name="sources">Sources list.</param>
    void ReplaceAll(IReadOnlyList<VideoSourceConfig> sources);

    /// <summary>
    /// Inserts or updates a single source.
    /// </summary>
    /// <param name="source">Source config.</param>
    void Upsert(VideoSourceConfig source);
}
