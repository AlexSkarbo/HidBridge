using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Persistence;

/// <summary>
/// Persists audit and telemetry streams in separate JSON files.
/// </summary>
public sealed class FileEventStore : IEventStore
{
    private readonly JsonFileStore<List<AuditEventBody>> _auditStore;
    private readonly JsonFileStore<List<TelemetryEventBody>> _telemetryStore;

    /// <summary>
    /// Initializes the event store.
    /// </summary>
    /// <param name="options">The file persistence options.</param>
    public FileEventStore(FilePersistenceOptions options)
    {
        _auditStore = new JsonFileStore<List<AuditEventBody>>(options.AuditPath, () => new List<AuditEventBody>());
        _telemetryStore = new JsonFileStore<List<TelemetryEventBody>>(options.TelemetryPath, () => new List<TelemetryEventBody>());
    }

    /// <summary>
    /// Appends one audit event to the persistent stream.
    /// </summary>
    public async Task AppendAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken)
    {
        var normalized = auditEvent.CreatedAtUtc.HasValue
            ? auditEvent
            : auditEvent with { CreatedAtUtc = DateTimeOffset.UtcNow };

        await _auditStore.UpdateAsync(items =>
        {
            var copy = items.ToList();
            copy.Add(normalized);
            return copy;
        }, cancellationToken);
    }

    /// <summary>
    /// Appends one telemetry event to the persistent stream.
    /// </summary>
    public async Task AppendTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken)
    {
        var normalized = telemetryEvent.CreatedAtUtc.HasValue
            ? telemetryEvent
            : telemetryEvent with { CreatedAtUtc = DateTimeOffset.UtcNow };

        await _telemetryStore.UpdateAsync(items =>
        {
            var copy = items.ToList();
            copy.Add(normalized);
            return copy;
        }, cancellationToken);
    }

    /// <summary>
    /// Lists the persisted audit events in storage order.
    /// </summary>
    public async Task<IReadOnlyList<AuditEventBody>> ListAuditAsync(CancellationToken cancellationToken)
    {
        var items = await _auditStore.ReadAsync(cancellationToken);
        return items.ToArray();
    }

    /// <summary>
    /// Lists the persisted telemetry events in storage order.
    /// </summary>
    public async Task<IReadOnlyList<TelemetryEventBody>> ListTelemetryAsync(CancellationToken cancellationToken)
    {
        var items = await _telemetryStore.ReadAsync(cancellationToken);
        return items.ToArray();
    }

    /// <summary>
    /// Removes audit events older than the specified cutoff and returns the number of deleted items.
    /// </summary>
    public async Task<int> TrimAuditAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        var deleted = 0;
        await _auditStore.UpdateAsync(items =>
        {
            var filtered = items
                .Where(x => (x.CreatedAtUtc ?? DateTimeOffset.UtcNow) >= olderThanUtc)
                .ToList();
            deleted = items.Count - filtered.Count;
            return filtered;
        }, cancellationToken);
        return deleted;
    }

    /// <summary>
    /// Removes telemetry events older than the specified cutoff and returns the number of deleted items.
    /// </summary>
    public async Task<int> TrimTelemetryAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        var deleted = 0;
        await _telemetryStore.UpdateAsync(items =>
        {
            var filtered = items
                .Where(x => (x.CreatedAtUtc ?? DateTimeOffset.UtcNow) >= olderThanUtc)
                .ToList();
            deleted = items.Count - filtered.Count;
            return filtered;
        }, cancellationToken);
        return deleted;
    }
}
