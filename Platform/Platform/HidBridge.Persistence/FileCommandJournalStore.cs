using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Persistence;

/// <summary>
/// Persists the command journal in a JSON file.
/// </summary>
public sealed class FileCommandJournalStore : ICommandJournalStore
{
    private readonly JsonFileStore<List<CommandJournalEntryBody>> _store;

    /// <summary>
    /// Initializes the file-backed command journal store.
    /// </summary>
    /// <param name="options">The file persistence options.</param>
    public FileCommandJournalStore(FilePersistenceOptions options)
    {
        _store = new JsonFileStore<List<CommandJournalEntryBody>>(options.CommandsPath, () => new List<CommandJournalEntryBody>());
    }

    /// <summary>
    /// Appends one command journal entry.
    /// </summary>
    public async Task AppendAsync(CommandJournalEntryBody entry, CancellationToken cancellationToken)
    {
        var normalized = entry.CreatedAtUtc == default
            ? entry with { CreatedAtUtc = DateTimeOffset.UtcNow }
            : entry;

        await _store.UpdateAsync(items =>
        {
            var copy = items.ToList();
            if (copy.Any(x => string.Equals(x.CommandId, normalized.CommandId, StringComparison.OrdinalIgnoreCase)))
            {
                return copy;
            }

            if (!string.IsNullOrWhiteSpace(normalized.IdempotencyKey) &&
                copy.Any(x =>
                    string.Equals(x.SessionId, normalized.SessionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.IdempotencyKey, normalized.IdempotencyKey, StringComparison.OrdinalIgnoreCase)))
            {
                return copy;
            }

            copy.Add(normalized);
            return copy;
        }, cancellationToken);
    }

    /// <summary>
    /// Resolves one command journal entry by command identifier.
    /// </summary>
    public async Task<CommandJournalEntryBody?> FindByCommandIdAsync(string commandId, CancellationToken cancellationToken)
    {
        var items = await _store.ReadAsync(cancellationToken);
        return items.FirstOrDefault(x => string.Equals(x.CommandId, commandId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves one command journal entry by session identifier and idempotency key.
    /// </summary>
    public async Task<CommandJournalEntryBody?> FindByIdempotencyKeyAsync(string sessionId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var items = await _store.ReadAsync(cancellationToken);
        return items.FirstOrDefault(x =>
            string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.IdempotencyKey, idempotencyKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lists all persisted command journal entries in storage order.
    /// </summary>
    public async Task<IReadOnlyList<CommandJournalEntryBody>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await _store.ReadAsync(cancellationToken);
        return items.ToArray();
    }

    /// <summary>
    /// Lists persisted command journal entries for one session in storage order.
    /// </summary>
    public async Task<IReadOnlyList<CommandJournalEntryBody>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var items = await _store.ReadAsync(cancellationToken);
        return items
            .Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
