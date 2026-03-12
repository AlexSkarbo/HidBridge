using HidBridge.Abstractions;
using HidBridge.Contracts;
using Microsoft.EntityFrameworkCore;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Persists audit and telemetry event streams in PostgreSQL.
/// </summary>
public sealed class SqlEventStore : IEventStore
{
    private readonly IDbContextFactory<PlatformDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes the SQL event store.
    /// </summary>
    /// <param name="dbContextFactory">Creates database contexts on demand.</param>
    public SqlEventStore(IDbContextFactory<PlatformDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Appends one audit event to the persistent stream.
    /// </summary>
    public async Task AppendAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(SqlStoreMapper.ToEntity(auditEvent));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Appends one telemetry event to the persistent stream.
    /// </summary>
    public async Task AppendTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.TelemetryEvents.Add(SqlStoreMapper.ToEntity(telemetryEvent));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Lists the persisted audit events in storage order.
    /// </summary>
    public async Task<IReadOnlyList<AuditEventBody>> ListAuditAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.AuditEvents
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return items.Select(SqlStoreMapper.ToBody).ToArray();
    }

    /// <summary>
    /// Lists the persisted telemetry events in storage order.
    /// </summary>
    public async Task<IReadOnlyList<TelemetryEventBody>> ListTelemetryAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.TelemetryEvents
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return items.Select(SqlStoreMapper.ToBody).ToArray();
    }

    /// <summary>
    /// Removes audit events older than the specified cutoff and returns the number of deleted items.
    /// </summary>
    public async Task<int> TrimAuditAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.AuditEvents
            .Where(x => x.CreatedAtUtc < olderThanUtc)
            .ToListAsync(cancellationToken);
        db.AuditEvents.RemoveRange(items);
        await db.SaveChangesAsync(cancellationToken);
        return items.Count;
    }

    /// <summary>
    /// Removes telemetry events older than the specified cutoff and returns the number of deleted items.
    /// </summary>
    public async Task<int> TrimTelemetryAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.TelemetryEvents
            .Where(x => x.CreatedAtUtc < olderThanUtc)
            .ToListAsync(cancellationToken);
        db.TelemetryEvents.RemoveRange(items);
        await db.SaveChangesAsync(cancellationToken);
        return items.Count;
    }
}
