using HidBridge.Abstractions;

namespace HidBridge.Persistence;

/// <summary>
/// Persists policy revision snapshots in JSON files.
/// </summary>
public sealed class FilePolicyRevisionStore : IPolicyRevisionStore
{
    private readonly JsonFileStore<List<PolicyRevisionSnapshot>> _store;

    /// <summary>
    /// Initializes the file-backed policy revision store.
    /// </summary>
    public FilePolicyRevisionStore(FilePersistenceOptions options)
    {
        _store = new JsonFileStore<List<PolicyRevisionSnapshot>>(options.PolicyRevisionsPath, () => new List<PolicyRevisionSnapshot>());
    }

    /// <summary>
    /// Appends one policy revision snapshot.
    /// </summary>
    public Task AppendAsync(PolicyRevisionSnapshot snapshot, CancellationToken cancellationToken)
        => _store.UpdateAsync(items => items
            .Append(snapshot)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenBy(x => x.RevisionId, StringComparer.OrdinalIgnoreCase)
            .ToList(), cancellationToken);

    /// <summary>
    /// Lists persisted policy revisions.
    /// </summary>
    public async Task<IReadOnlyList<PolicyRevisionSnapshot>> ListAsync(CancellationToken cancellationToken)
        => (await _store.ReadAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenBy(x => x.RevisionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Prunes revisions outside the retention window while keeping the newest revisions per entity.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset retainSinceUtc, int maxRevisionsPerEntity, CancellationToken cancellationToken)
    {
        var deleted = 0;
        await _store.UpdateAsync(items =>
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in items.GroupBy(static item => (item.EntityType, item.EntityId)))
            {
                var ordered = group
                    .OrderByDescending(static x => x.CreatedAtUtc)
                    .ThenByDescending(static x => x.Version)
                    .ThenBy(static x => x.RevisionId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (var item in ordered.Take(Math.Max(maxRevisionsPerEntity, 1)))
                {
                    keep.Add(item.RevisionId);
                }

                foreach (var item in ordered.Where(x => x.CreatedAtUtc >= retainSinceUtc))
                {
                    keep.Add(item.RevisionId);
                }
            }

            var retained = items
                .Where(item => keep.Contains(item.RevisionId))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenBy(x => x.RevisionId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            deleted = items.Count - retained.Count;
            return retained;
        }, cancellationToken);

        return deleted;
    }
}
