using HidBridge.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Persists endpoint snapshots in PostgreSQL.
/// </summary>
public sealed class SqlEndpointSnapshotStore : IEndpointSnapshotStore
{
    private readonly IDbContextFactory<PlatformDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes the SQL endpoint snapshot store.
    /// </summary>
    /// <param name="dbContextFactory">Creates database contexts on demand.</param>
    public SqlEndpointSnapshotStore(IDbContextFactory<PlatformDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Creates or updates one endpoint snapshot.
    /// </summary>
    public async Task UpsertAsync(EndpointSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Endpoints
            .Include(x => x.Capabilities)
            .FirstOrDefaultAsync(x => x.EndpointId == snapshot.EndpointId, cancellationToken);

        if (existing is not null)
        {
            db.EndpointCapabilities.RemoveRange(existing.Capabilities);
            db.Endpoints.Remove(existing);
        }

        db.Endpoints.Add(SqlStoreMapper.ToEntity(snapshot));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Lists all persisted endpoint snapshots.
    /// </summary>
    public async Task<IReadOnlyList<EndpointSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.Endpoints
            .Include(x => x.Capabilities)
            .OrderBy(x => x.EndpointId)
            .ToListAsync(cancellationToken);
        return items.Select(SqlStoreMapper.ToSnapshot).ToArray();
    }
}
