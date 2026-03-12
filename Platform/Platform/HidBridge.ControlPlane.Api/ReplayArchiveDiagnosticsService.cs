using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Builds replay-oriented and archive-oriented diagnostics views over persisted session, event, and command data.
/// </summary>
public sealed class ReplayArchiveDiagnosticsService
{
    private readonly ISessionStore _sessionStore;
    private readonly IEventStore _eventStore;
    private readonly ICommandJournalStore _commandJournalStore;

    /// <summary>
    /// Creates the replay/archive diagnostics service.
    /// </summary>
    /// <param name="sessionStore">Provides persisted session snapshots.</param>
    /// <param name="eventStore">Provides persisted audit and telemetry streams.</param>
    /// <param name="commandJournalStore">Provides persisted command journal records.</param>
    public ReplayArchiveDiagnosticsService(
        ISessionStore sessionStore,
        IEventStore eventStore,
        ICommandJournalStore commandJournalStore)
    {
        _sessionStore = sessionStore;
        _eventStore = eventStore;
        _commandJournalStore = commandJournalStore;
    }

    /// <summary>
    /// Builds one replay bundle for the specified session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="take">The maximum number of entries to include in each slice.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The replay bundle read model.</returns>
    public async Task<SessionReplayBundleReadModel> GetSessionReplayBundleAsync(
        string sessionId,
        ApiCallerContext? caller,
        int take,
        CancellationToken cancellationToken)
    {
        var normalizedTake = NormalizeTake(take);
        var session = await RequireSessionAsync(sessionId, cancellationToken);
        EnsureVisibleSession(session, caller);
        var audit = (await _eventStore.ListAuditAsync(cancellationToken))
            .Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .Take(normalizedTake)
            .ToArray();
        var telemetry = (await _eventStore.ListTelemetryAsync(cancellationToken))
            .Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .Take(normalizedTake)
            .ToArray();
        var commands = (await _commandJournalStore.ListBySessionAsync(sessionId, cancellationToken))
            .OrderByDescending(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
            .Take(normalizedTake)
            .ToArray();
        var timeline = TimelineComposer.Compose(audit, telemetry, commands, sessionId, normalizedTake);

        return new SessionReplayBundleReadModel(
            sessionId,
            session,
            audit.Length,
            telemetry.Length,
            commands.Length,
            audit,
            telemetry,
            commands,
            timeline);
    }

    /// <summary>
    /// Builds one archive diagnostics summary over the persisted stores.
    /// </summary>
    /// <param name="sessionId">Optional session filter.</param>
    /// <param name="sinceUtc">Optional lower time bound.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The archive summary read model.</returns>
    public async Task<ArchiveDiagnosticsSummaryReadModel> GetArchiveSummaryAsync(
        string? sessionId,
        ApiCallerContext? caller,
        DateTimeOffset? sinceUtc,
        CancellationToken cancellationToken)
    {
        var visibleSessionIds = await GetVisibleSessionIdsAsync(caller, cancellationToken);
        var audit = FilterAudit(await _eventStore.ListAuditAsync(cancellationToken), sessionId, null, sinceUtc, visibleSessionIds, caller).ToArray();
        var telemetry = FilterTelemetry(await _eventStore.ListTelemetryAsync(cancellationToken), sessionId, null, sinceUtc, visibleSessionIds, caller).ToArray();
        var commands = FilterCommands(await _commandJournalStore.ListAsync(cancellationToken), sessionId, null, sinceUtc, visibleSessionIds, caller).ToArray();

        return new ArchiveDiagnosticsSummaryReadModel(
            DateTimeOffset.UtcNow,
            sessionId,
            sinceUtc,
            audit.Length,
            telemetry.Length,
            commands.Length,
            Earliest(audit.Select(x => x.CreatedAtUtc)),
            Latest(audit.Select(x => x.CreatedAtUtc)),
            Earliest(telemetry.Select(x => x.CreatedAtUtc)),
            Latest(telemetry.Select(x => x.CreatedAtUtc)),
            Earliest(commands.Select(x => (DateTimeOffset?)x.CreatedAtUtc)),
            Latest(commands.Select(x => (DateTimeOffset?)(x.CompletedAtUtc ?? x.CreatedAtUtc))));
    }

    /// <summary>
    /// Returns a paged archive audit slice suitable for diagnostics export and replay preparation.
    /// </summary>
    public async Task<ProjectionPage<AuditProjectionItemReadModel>> QueryArchiveAuditAsync(
        string? sessionId,
        string? category,
        string? principalId,
        DateTimeOffset? sinceUtc,
        ApiCallerContext? caller,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var visibleSessionIds = await GetVisibleSessionIdsAsync(caller, cancellationToken);
        var items = FilterAudit(await _eventStore.ListAuditAsync(cancellationToken), sessionId, category, sinceUtc, visibleSessionIds, caller)
            .Select(x => new AuditProjectionItemReadModel(
                x.Category,
                x.Message,
                x.SessionId,
                ResolvePrincipalId(x.Data),
                x.CreatedAtUtc ?? DateTimeOffset.MinValue,
                x.Data))
            .Where(x => string.IsNullOrWhiteSpace(principalId) || string.Equals(x.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Page(items, skip, take);
    }

    /// <summary>
    /// Returns a paged archive telemetry slice suitable for diagnostics export and replay preparation.
    /// </summary>
    public async Task<ProjectionPage<TelemetryProjectionItemReadModel>> QueryArchiveTelemetryAsync(
        string? sessionId,
        string? scope,
        string? metricName,
        DateTimeOffset? sinceUtc,
        ApiCallerContext? caller,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var visibleSessionIds = await GetVisibleSessionIdsAsync(caller, cancellationToken);
        var items = FilterTelemetry(await _eventStore.ListTelemetryAsync(cancellationToken), sessionId, scope, sinceUtc, visibleSessionIds, caller)
            .Where(x => string.IsNullOrWhiteSpace(metricName) || x.Metrics.Keys.Any(key => string.Equals(key, metricName, StringComparison.OrdinalIgnoreCase)))
            .Select(x => new TelemetryProjectionItemReadModel(
                x.Scope,
                x.SessionId,
                x.CreatedAtUtc ?? DateTimeOffset.MinValue,
                x.Metrics.ToDictionary(metric => metric.Key, metric => FormatMetricValue(metric.Value), StringComparer.OrdinalIgnoreCase),
                x.Metrics
                    .Where(metric => TryConvertMetricValue(metric.Value).HasValue)
                    .ToDictionary(metric => metric.Key, metric => TryConvertMetricValue(metric.Value)!.Value, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Page(items, skip, take);
    }

    /// <summary>
    /// Returns a paged archive command slice suitable for diagnostics export and replay preparation.
    /// </summary>
    public async Task<ProjectionPage<CommandJournalEntryBody>> QueryArchiveCommandsAsync(
        string? sessionId,
        CommandStatus? status,
        DateTimeOffset? sinceUtc,
        ApiCallerContext? caller,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var visibleSessionIds = await GetVisibleSessionIdsAsync(caller, cancellationToken);
        var items = FilterCommands(await _commandJournalStore.ListAsync(cancellationToken), sessionId, status, sinceUtc, visibleSessionIds, caller)
            .OrderByDescending(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
            .ThenBy(x => x.CommandId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Page(items, skip, take);
    }

    private async Task<SessionSnapshot> RequireSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = (await _sessionStore.ListAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        return session ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");
    }

    private static ProjectionPage<TItem> Page<TItem>(IReadOnlyList<TItem> items, int skip, int take)
    {
        var normalizedSkip = Math.Max(skip, 0);
        var normalizedTake = NormalizeTake(take);
        return new ProjectionPage<TItem>(
            items.Count,
            normalizedSkip,
            normalizedTake,
            items.Skip(normalizedSkip).Take(normalizedTake).ToArray());
    }

    private static IEnumerable<AuditEventBody> FilterAudit(
        IReadOnlyList<AuditEventBody> items,
        string? sessionId,
        string? category,
        DateTimeOffset? sinceUtc,
        HashSet<string>? visibleSessionIds,
        ApiCallerContext? caller)
    {
        return items
            .Where(x => IsVisibleSessionBoundRecord(x.SessionId, visibleSessionIds, caller))
            .Where(x => string.IsNullOrWhiteSpace(sessionId) || string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(category) || string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(x => !sinceUtc.HasValue || (x.CreatedAtUtc ?? DateTimeOffset.MinValue) >= sinceUtc.Value);
    }

    private static IEnumerable<TelemetryEventBody> FilterTelemetry(
        IReadOnlyList<TelemetryEventBody> items,
        string? sessionId,
        string? scope,
        DateTimeOffset? sinceUtc,
        HashSet<string>? visibleSessionIds,
        ApiCallerContext? caller)
    {
        return items
            .Where(x => IsVisibleSessionBoundRecord(x.SessionId, visibleSessionIds, caller))
            .Where(x => string.IsNullOrWhiteSpace(sessionId) || string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(scope) || string.Equals(x.Scope, scope, StringComparison.OrdinalIgnoreCase))
            .Where(x => !sinceUtc.HasValue || (x.CreatedAtUtc ?? DateTimeOffset.MinValue) >= sinceUtc.Value);
    }

    private static IEnumerable<CommandJournalEntryBody> FilterCommands(
        IReadOnlyList<CommandJournalEntryBody> items,
        string? sessionId,
        CommandStatus? status,
        DateTimeOffset? sinceUtc,
        HashSet<string>? visibleSessionIds,
        ApiCallerContext? caller)
    {
        return items
            .Where(x => IsVisibleSessionBoundRecord(x.SessionId, visibleSessionIds, caller))
            .Where(x => string.IsNullOrWhiteSpace(sessionId) || string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Where(x => status is null || x.Status == status)
            .Where(x => !sinceUtc.HasValue || x.CreatedAtUtc >= sinceUtc.Value);
    }

    private static int NormalizeTake(int take) => Math.Clamp(take, 1, 500);

    private static DateTimeOffset? Earliest(IEnumerable<DateTimeOffset?> values)
        => values.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).FirstOrDefault();

    private static DateTimeOffset? Latest(IEnumerable<DateTimeOffset?> values)
        => values.Where(x => x.HasValue).Select(x => x!.Value).OrderByDescending(x => x).FirstOrDefault();

    private static string? ResolvePrincipalId(IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null)
        {
            return null;
        }

        return data.TryGetValue("principalId", out var value)
            ? value?.ToString()
            : null;
    }

    private static string FormatMetricValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            System.Text.Json.JsonElement element => element.ToString(),
            DateTimeOffset dto => dto.ToString("O"),
            DateTime dt => dt.ToString("O"),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static double? TryConvertMetricValue(object? value)
    {
        return value switch
        {
            byte number => number,
            sbyte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            float number => number,
            double number => number,
            decimal number => (double)number,
            System.Text.Json.JsonElement element when element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetDouble(out var parsedNumber) => parsedNumber,
            System.Text.Json.JsonElement element when element.ValueKind == System.Text.Json.JsonValueKind.String && double.TryParse(element.GetString(), out var parsedTextNumber) => parsedTextNumber,
            string text when double.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static void EnsureVisibleSession(SessionSnapshot snapshot, ApiCallerContext? caller)
    {
        if (caller is null || !caller.IsPresent)
        {
            return;
        }

        caller.EnsureSessionScope(snapshot);
    }

    private async Task<HashSet<string>?> GetVisibleSessionIdsAsync(ApiCallerContext? caller, CancellationToken cancellationToken)
    {
        if (caller is null || !caller.IsPresent)
        {
            return null;
        }

        return (await _sessionStore.ListAsync(cancellationToken))
            .Where(caller.CanAccessSession)
            .Select(snapshot => snapshot.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsVisibleSessionBoundRecord(string? sessionId, HashSet<string>? visibleSessionIds, ApiCallerContext? caller)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return caller?.CanAccessGlobalFleetData ?? true;
        }

        return visibleSessionIds?.Contains(sessionId) ?? true;
    }
}
