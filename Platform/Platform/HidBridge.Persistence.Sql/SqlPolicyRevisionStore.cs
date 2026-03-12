using HidBridge.Abstractions;
using HidBridge.Persistence.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Persists policy revision snapshots in PostgreSQL.
/// </summary>
public sealed class SqlPolicyRevisionStore : IPolicyRevisionStore
{
    private readonly IDbContextFactory<PlatformDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes the SQL policy revision store.
    /// </summary>
    public SqlPolicyRevisionStore(IDbContextFactory<PlatformDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Appends one policy revision snapshot.
    /// </summary>
    public async Task AppendAsync(PolicyRevisionSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.PolicyRevisions.Add(new PolicyRevisionEntity
        {
            RevisionId = snapshot.RevisionId,
            EntityType = snapshot.EntityType,
            EntityId = snapshot.EntityId,
            ScopeId = snapshot.ScopeId,
            PrincipalId = snapshot.PrincipalId,
            Version = snapshot.Version,
            SnapshotJson = snapshot.SnapshotJson,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            Source = snapshot.Source,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Lists persisted policy revisions.
    /// </summary>
    public async Task<IReadOnlyList<PolicyRevisionSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await dbContext.PolicyRevisions
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.RevisionId)
            .ToArrayAsync(cancellationToken);

        return entities
            .Select(static entity => new PolicyRevisionSnapshot(
                entity.RevisionId,
                entity.EntityType,
                entity.EntityId,
                entity.ScopeId,
                entity.PrincipalId,
                entity.Version,
                entity.SnapshotJson,
                entity.CreatedAtUtc,
                entity.Source))
            .ToArray();
    }

    /// <summary>
    /// Prunes revisions outside the retention window while keeping the newest revisions per entity.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset retainSinceUtc, int maxRevisionsPerEntity, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await dbContext.PolicyRevisions
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Version)
            .ThenBy(x => x.RevisionId)
            .ToArrayAsync(cancellationToken);

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in entities.GroupBy(static item => (item.EntityType, item.EntityId)))
        {
            foreach (var item in group.Take(Math.Max(maxRevisionsPerEntity, 1)))
            {
                keep.Add(item.RevisionId);
            }

            foreach (var item in group.Where(x => x.CreatedAtUtc >= retainSinceUtc))
            {
                keep.Add(item.RevisionId);
            }
        }

        var toDelete = entities.Where(item => !keep.Contains(item.RevisionId)).ToArray();
        if (toDelete.Length == 0)
        {
            return 0;
        }

        dbContext.PolicyRevisions.RemoveRange(toDelete);
        await dbContext.SaveChangesAsync(cancellationToken);
        return toDelete.Length;
    }
}
