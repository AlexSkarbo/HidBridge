using HidControl.Application.Abstractions;
using HidControl.Contracts;
using HidControl.UseCases;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts VideoSourceStore to application abstraction.
/// </summary>
public sealed class VideoSourceStoreAdapter : IVideoSourceStore
{
    private readonly VideoSourceStore _store;

    /// <summary>
    /// Executes VideoSourceStoreAdapter.
    /// </summary>
    /// <param name="store">Store.</param>
    public VideoSourceStoreAdapter(VideoSourceStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Gets all sources.
    /// </summary>
    /// <returns>Sources list.</returns>
    public IReadOnlyList<VideoSourceConfig> GetAll() => _store.GetAll();

    /// <summary>
    /// Replaces all sources.
    /// </summary>
    /// <param name="sources">Sources.</param>
    public void ReplaceAll(IReadOnlyList<VideoSourceConfig> sources) => _store.ReplaceAll(sources);

    /// <summary>
    /// Inserts or updates a source.
    /// </summary>
    /// <param name="source">Source.</param>
    public void Upsert(VideoSourceConfig source) => _store.Upsert(source);
}
