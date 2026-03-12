using HidBridge.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Persists session snapshots in PostgreSQL.
/// </summary>
public sealed class SqlSessionStore : ISessionStore
{
    private readonly IDbContextFactory<PlatformDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes the SQL session store.
    /// </summary>
    /// <param name="dbContextFactory">Creates database contexts on demand.</param>
    public SqlSessionStore(IDbContextFactory<PlatformDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Creates or updates one session snapshot.
    /// </summary>
    public async Task UpsertAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Sessions
            .Include(x => x.Participants)
            .Include(x => x.Shares)
            .FirstOrDefaultAsync(x => x.SessionId == snapshot.SessionId, cancellationToken);
        if (existing is not null)
        {
            db.Sessions.Remove(existing);
        }

        db.Sessions.Add(SqlStoreMapper.ToEntity(snapshot));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Lists all persisted session snapshots.
    /// </summary>
    public async Task<IReadOnlyList<SessionSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.Sessions
            .Include(x => x.Participants)
            .Include(x => x.Shares)
            .OrderBy(x => x.SessionId)
            .ToListAsync(cancellationToken);
        return items.Select(SqlStoreMapper.ToSnapshot).ToArray();
    }

    /// <summary>
    /// Removes one session snapshot from storage.
    /// </summary>
    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Sessions
            .Include(x => x.Participants)
            .Include(x => x.Shares)
            .FirstOrDefaultAsync(x => x.SessionId == sessionId, cancellationToken);
        if (existing is null)
        {
            return;
        }

        db.Sessions.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}
