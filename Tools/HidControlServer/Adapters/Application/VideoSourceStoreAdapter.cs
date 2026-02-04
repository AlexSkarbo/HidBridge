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
}
