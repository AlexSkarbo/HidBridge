using HidBridge.Abstractions;

namespace HidBridge.Persistence;

/// <summary>
/// Persists endpoint capability snapshots in a JSON file.
/// </summary>
public sealed class FileEndpointSnapshotStore : IEndpointSnapshotStore
{
    private readonly JsonFileStore<List<EndpointSnapshot>> _store;

    /// <summary>
    /// Initializes the endpoint snapshot store.
    /// </summary>
    /// <param name="options">The file persistence options.</param>
    public FileEndpointSnapshotStore(FilePersistenceOptions options)
    {
        _store = new JsonFileStore<List<EndpointSnapshot>>(options.EndpointsPath, () => new List<EndpointSnapshot>());
    }

    /// <summary>
    /// Creates or updates one endpoint snapshot.
    /// </summary>
    public async Task UpsertAsync(EndpointSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _store.UpdateAsync(items =>
        {
            var copy = items
                .Where(x => !string.Equals(x.EndpointId, snapshot.EndpointId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            copy.Add(snapshot);
            return copy
                .OrderBy(x => x.EndpointId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// Lists all persisted endpoint snapshots.
    /// </summary>
    public async Task<IReadOnlyList<EndpointSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await _store.ReadAsync(cancellationToken);
        return items
            .OrderBy(x => x.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
