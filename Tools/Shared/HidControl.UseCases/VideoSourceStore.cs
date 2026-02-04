using HidControl.Contracts;

namespace HidControl.UseCases;

// Thread-safe in-memory store for video sources.
/// <summary>
/// Stores video source data.
/// </summary>
public sealed class VideoSourceStore
{
    private readonly object _lock = new();
    private readonly List<VideoSourceConfig> _sources = new();

    /// <summary>
    /// Executes VideoSourceStore.
    /// </summary>
    /// <param name="initial">The initial.</param>
    public VideoSourceStore(IEnumerable<VideoSourceConfig>? initial)
    {
        if (initial is null) return;
        _sources.AddRange(initial);
    }

    /// <summary>
    /// Gets all.
    /// </summary>
    /// <returns>Result.</returns>
    public IReadOnlyList<VideoSourceConfig> GetAll()
    {
        lock (_lock)
        {
            return _sources.ToArray();
        }
    }

    /// <summary>
    /// Executes ReplaceAll.
    /// </summary>
    /// <param name="sources">The sources.</param>
    public void ReplaceAll(IEnumerable<VideoSourceConfig> sources)
    {
        lock (_lock)
        {
            _sources.Clear();
            _sources.AddRange(sources);
        }
    }

    /// <summary>
    /// Executes Upsert.
    /// </summary>
    /// <param name="source">The source.</param>
    public void Upsert(VideoSourceConfig source)
    {
        lock (_lock)
        {
            int idx = _sources.FindIndex(s => string.Equals(s.Id, source.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _sources[idx] = source;
            }
            else
            {
                _sources.Add(source);
            }
        }
    }
}
