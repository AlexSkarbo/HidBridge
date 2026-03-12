using HidBridge.Abstractions;

namespace HidBridge.Persistence;

/// <summary>
/// Persists connector descriptors in a JSON file.
/// </summary>
public sealed class FileConnectorCatalogStore : IConnectorCatalogStore
{
    private readonly JsonFileStore<List<ConnectorDescriptor>> _store;

    /// <summary>
    /// Initializes the connector catalog store.
    /// </summary>
    /// <param name="options">The file persistence options.</param>
    public FileConnectorCatalogStore(FilePersistenceOptions options)
    {
        _store = new JsonFileStore<List<ConnectorDescriptor>>(options.ConnectorsPath, () => new List<ConnectorDescriptor>());
    }

    /// <summary>
    /// Creates or updates one connector descriptor snapshot.
    /// </summary>
    public async Task UpsertAsync(ConnectorDescriptor descriptor, CancellationToken cancellationToken)
    {
        await _store.UpdateAsync(items =>
        {
            var copy = items
                .Where(x => !string.Equals(x.AgentId, descriptor.AgentId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            copy.Add(descriptor);
            return copy
                .OrderBy(x => x.AgentId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// Lists all persisted connector descriptors.
    /// </summary>
    public async Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await _store.ReadAsync(cancellationToken);
        return items
            .OrderBy(x => x.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
