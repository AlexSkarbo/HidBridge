using HidBridge.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Persists connector descriptors in PostgreSQL.
/// </summary>
public sealed class SqlConnectorCatalogStore : IConnectorCatalogStore
{
    private readonly IDbContextFactory<PlatformDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes the SQL connector catalog store.
    /// </summary>
    /// <param name="dbContextFactory">Creates database contexts on demand.</param>
    public SqlConnectorCatalogStore(IDbContextFactory<PlatformDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Creates or updates one connector descriptor snapshot.
    /// </summary>
    public async Task UpsertAsync(ConnectorDescriptor descriptor, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Agents
            .Include(x => x.Capabilities)
            .FirstOrDefaultAsync(x => x.AgentId == descriptor.AgentId, cancellationToken);

        if (existing is not null)
        {
            db.AgentCapabilities.RemoveRange(existing.Capabilities);
            db.Agents.Remove(existing);
        }

        db.Agents.Add(SqlStoreMapper.ToEntity(descriptor));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Lists all persisted connector descriptors.
    /// </summary>
    public async Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.Agents
            .Include(x => x.Capabilities)
            .OrderBy(x => x.AgentId)
            .ToListAsync(cancellationToken);
        return items.Select(SqlStoreMapper.ToDescriptor).ToArray();
    }
}
