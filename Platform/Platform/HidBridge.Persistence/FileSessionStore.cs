using HidBridge.Abstractions;

namespace HidBridge.Persistence;

/// <summary>
/// Persists session snapshots in a JSON file.
/// </summary>
public sealed class FileSessionStore : ISessionStore
{
    private readonly JsonFileStore<List<SessionSnapshot>> _store;

    /// <summary>
    /// Initializes the session store.
    /// </summary>
    /// <param name="options">The file persistence options.</param>
    public FileSessionStore(FilePersistenceOptions options)
    {
        _store = new JsonFileStore<List<SessionSnapshot>>(options.SessionsPath, () => new List<SessionSnapshot>());
    }

    /// <summary>
    /// Creates or updates one session snapshot.
    /// </summary>
    public async Task UpsertAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _store.UpdateAsync(items =>
        {
            var copy = items
                .Where(x => !string.Equals(x.SessionId, snapshot.SessionId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            copy.Add(snapshot);
            return copy
                .OrderBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// Removes one session snapshot from storage.
    /// </summary>
    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _store.UpdateAsync(items => items
            .Where(x => !string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToList(), cancellationToken);
    }

    /// <summary>
    /// Lists all persisted session snapshots.
    /// </summary>
    public async Task<IReadOnlyList<SessionSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await _store.ReadAsync(cancellationToken);
        return items
            .OrderBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
