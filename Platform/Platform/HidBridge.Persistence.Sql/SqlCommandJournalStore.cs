using HidBridge.Abstractions;
using HidBridge.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Persists command journal entries in PostgreSQL.
/// </summary>
public sealed class SqlCommandJournalStore : ICommandJournalStore
{
    private readonly IDbContextFactory<PlatformDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes the SQL-backed command journal store.
    /// </summary>
    /// <param name="dbContextFactory">Creates database contexts on demand.</param>
    public SqlCommandJournalStore(IDbContextFactory<PlatformDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Appends one command journal entry.
    /// </summary>
    public async Task AppendAsync(CommandJournalEntryBody entry, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.CommandJournal.AnyAsync(x => x.CommandId == entry.CommandId, cancellationToken))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.IdempotencyKey) &&
            await db.CommandJournal.AnyAsync(
                x => x.SessionId == entry.SessionId && x.IdempotencyKey == entry.IdempotencyKey,
                cancellationToken))
        {
            return;
        }

        db.CommandJournal.Add(SqlStoreMapper.ToEntity(entry));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateCommandKey(ex))
        {
            // A concurrent caller already persisted the same command journal entry.
            // The command journal is idempotent, so a duplicate insert is treated as success.
        }
    }

    /// <summary>
    /// Resolves one persisted command journal entry by its unique command identifier.
    /// </summary>
    public async Task<CommandJournalEntryBody?> FindByCommandIdAsync(string commandId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.CommandJournal
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.CommandId == commandId, cancellationToken);
        return entity is null ? null : SqlStoreMapper.ToBody(entity);
    }

    /// <summary>
    /// Resolves one persisted command journal entry by session identifier and idempotency key.
    /// </summary>
    public async Task<CommandJournalEntryBody?> FindByIdempotencyKeyAsync(string sessionId, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.CommandJournal
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(
                x => x.SessionId == sessionId && x.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return entity is null ? null : SqlStoreMapper.ToBody(entity);
    }

    /// <summary>
    /// Lists all persisted command journal entries in storage order.
    /// </summary>
    public async Task<IReadOnlyList<CommandJournalEntryBody>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.CommandJournal
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return items.Select(SqlStoreMapper.ToBody).ToArray();
    }

    /// <summary>
    /// Lists persisted command journal entries for one session in storage order.
    /// </summary>
    public async Task<IReadOnlyList<CommandJournalEntryBody>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.CommandJournal
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return items.Select(SqlStoreMapper.ToBody).ToArray();
    }

    private static bool IsDuplicateCommandKey(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException postgres &&
               postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
               string.Equals(postgres.ConstraintName, "IX_command_journal_CommandId", StringComparison.Ordinal);
    }
}
