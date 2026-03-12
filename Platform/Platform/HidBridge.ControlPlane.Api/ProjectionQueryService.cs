using HidBridge.Abstractions;
using HidBridge.Contracts;
using System.Text.Json;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Builds filterable and paged operator projections over sessions, endpoints, audit, and telemetry.
/// </summary>
public sealed class ProjectionQueryService
{
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly IEndpointSnapshotStore _endpointStore;
    private readonly ISessionStore _sessionStore;
    private readonly IEventStore _eventStore;

    /// <summary>
    /// Creates the projection query service.
    /// </summary>
    /// <param name="connectorRegistry">Provides registered connector descriptors.</param>
    /// <param name="endpointStore">Provides persisted endpoint snapshots.</param>
    /// <param name="sessionStore">Provides persisted session snapshots.</param>
    /// <param name="eventStore">Provides persisted audit and telemetry events.</param>
    public ProjectionQueryService(
        IConnectorRegistry connectorRegistry,
        IEndpointSnapshotStore endpointStore,
        ISessionStore sessionStore,
        IEventStore eventStore)
    {
        _connectorRegistry = connectorRegistry;
        _endpointStore = endpointStore;
        _sessionStore = sessionStore;
        _eventStore = eventStore;
    }

    /// <summary>
    /// Returns a paged session projection with optional filters.
    /// </summary>
    public async Task<ProjectionPage<SessionProjectionItemReadModel>> QuerySessionsAsync(
        SessionState? state,
        string? agentId,
        string? endpointId,
        string? principalId,
        ApiCallerContext? caller,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var sessions = await _sessionStore.ListAsync(cancellationToken);
        var items = FilterVisibleSessions(sessions, caller)
            .Where(x => state is null || x.State == state)
            .Where(x => string.IsNullOrWhiteSpace(agentId) || string.Equals(x.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(endpointId) || string.Equals(x.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            .Where(x => MatchesPrincipal(x, principalId))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var total = items.Length;
        var page = items
            .Skip(NormalizeSkip(skip))
            .Take(NormalizeTake(take))
            .Select(x => new SessionProjectionItemReadModel(
                x.SessionId,
                x.AgentId,
                x.EndpointId,
                x.TenantId,
                x.OrganizationId,
                x.Profile,
                x.Role,
                x.State,
                x.RequestedBy,
                x.Participants?.Count ?? 0,
                x.Shares?.Count ?? 0,
                x.Shares?.Count(share => share.Status == SessionShareStatus.Accepted) ?? 0,
                x.ControlLease?.PrincipalId,
                x.UpdatedAtUtc,
                x.LastHeartbeatAtUtc,
                x.LeaseExpiresAtUtc))
            .ToArray();

        return new ProjectionPage<SessionProjectionItemReadModel>(total, NormalizeSkip(skip), NormalizeTake(take), page);
    }

    /// <summary>
    /// Returns a paged endpoint projection with optional filters.
    /// </summary>
    public async Task<ProjectionPage<EndpointProjectionItemReadModel>> QueryEndpointsAsync(
        string? status,
        string? agentId,
        ConnectorType? connectorType,
        bool? activeOnly,
        ApiCallerContext? caller,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var connectors = await _connectorRegistry.ListAsync(cancellationToken);
        var endpoints = await _endpointStore.ListAsync(cancellationToken);
        var sessions = FilterVisibleSessions(await _sessionStore.ListAsync(cancellationToken), caller).ToArray();

        var connectorByEndpoint = connectors
            .GroupBy(x => x.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var latestSessionByEndpoint = sessions
            .GroupBy(x => x.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(session => session.UpdatedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        var items = endpoints
            .Select(endpoint =>
            {
                connectorByEndpoint.TryGetValue(endpoint.EndpointId, out var connector);
                latestSessionByEndpoint.TryGetValue(endpoint.EndpointId, out var session);
                var resolvedStatus = ResolveEndpointStatus(connector, session);
                return new EndpointProjectionItemReadModel(
                    endpoint.EndpointId,
                    connector?.AgentId,
                    connector?.ConnectorType,
                    resolvedStatus,
                    endpoint.Capabilities.Count,
                    endpoint.Capabilities.Select(capability => capability.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    session?.SessionId,
                    session?.State,
                    session?.ControlLease?.PrincipalId,
                    endpoint.UpdatedAtUtc);
            })
            .Where(x => string.IsNullOrWhiteSpace(status) || string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(agentId) || string.Equals(x.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .Where(x => connectorType is null || x.ConnectorType == connectorType)
            .Where(x => activeOnly is not true || !string.IsNullOrWhiteSpace(x.ActiveSessionId))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenBy(x => x.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var total = items.Length;
        var page = items.Skip(NormalizeSkip(skip)).Take(NormalizeTake(take)).ToArray();
        return new ProjectionPage<EndpointProjectionItemReadModel>(total, NormalizeSkip(skip), NormalizeTake(take), page);
    }

    /// <summary>
    /// Returns a paged audit projection with optional filters.
    /// </summary>
    public async Task<ProjectionPage<AuditProjectionItemReadModel>> QueryAuditAsync(
        string? category,
        string? sessionId,
        string? principalId,
        DateTimeOffset? sinceUtc,
        ApiCallerContext? caller,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var visibleSessionIds = await GetVisibleSessionIdsAsync(caller, cancellationToken);
        var items = (await _eventStore.ListAuditAsync(cancellationToken))
            .Where(x => IsVisibleSessionBoundRecord(x.SessionId, visibleSessionIds, caller))
            .Where(x => string.IsNullOrWhiteSpace(category) || string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(sessionId) || string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Where(x => !sinceUtc.HasValue || (x.CreatedAtUtc ?? DateTimeOffset.MinValue) >= sinceUtc.Value)
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

        var total = items.Length;
        var page = items.Skip(NormalizeSkip(skip)).Take(NormalizeTake(take)).ToArray();
        return new ProjectionPage<AuditProjectionItemReadModel>(total, NormalizeSkip(skip), NormalizeTake(take), page);
    }

    /// <summary>
    /// Returns a paged telemetry projection with optional filters.
    /// </summary>
    public async Task<ProjectionPage<TelemetryProjectionItemReadModel>> QueryTelemetryAsync(
        string? scope,
        string? sessionId,
        string? metricName,
        DateTimeOffset? sinceUtc,
        ApiCallerContext? caller,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var visibleSessionIds = await GetVisibleSessionIdsAsync(caller, cancellationToken);
        var items = (await _eventStore.ListTelemetryAsync(cancellationToken))
            .Where(x => IsVisibleSessionBoundRecord(x.SessionId, visibleSessionIds, caller))
            .Where(x => string.IsNullOrWhiteSpace(scope) || string.Equals(x.Scope, scope, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(sessionId) || string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Where(x => !sinceUtc.HasValue || (x.CreatedAtUtc ?? DateTimeOffset.MinValue) >= sinceUtc.Value)
            .Where(x => string.IsNullOrWhiteSpace(metricName) || x.Metrics.Keys.Any(key => string.Equals(key, metricName, StringComparison.OrdinalIgnoreCase)))
            .Select(x => new TelemetryProjectionItemReadModel(
                x.Scope,
                x.SessionId,
                x.CreatedAtUtc ?? DateTimeOffset.MinValue,
                x.Metrics
                    .OrderBy(metric => metric.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(metric => metric.Key, metric => FormatMetricValue(metric.Value), StringComparer.OrdinalIgnoreCase),
                x.Metrics
                    .Where(metric => TryConvertMetricValue(metric.Value).HasValue)
                    .ToDictionary(metric => metric.Key, metric => TryConvertMetricValue(metric.Value)!.Value, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.OccurredAtUtc)
            .ThenBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var total = items.Length;
        var page = items.Skip(NormalizeSkip(skip)).Take(NormalizeTake(take)).ToArray();
        return new ProjectionPage<TelemetryProjectionItemReadModel>(total, NormalizeSkip(skip), NormalizeTake(take), page);
    }

    private static int NormalizeSkip(int skip) => Math.Max(skip, 0);

    private static int NormalizeTake(int take) => Math.Clamp(take, 1, 500);

    private static bool MatchesPrincipal(SessionSnapshot snapshot, string? principalId)
    {
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return true;
        }

        return string.Equals(snapshot.RequestedBy, principalId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.ControlLease?.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase)
            || (snapshot.Participants?.Any(x => string.Equals(x.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase)) ?? false)
            || (snapshot.Shares?.Any(x => string.Equals(x.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

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

    private static string ResolveEndpointStatus(ConnectorDescriptor? connector, SessionSnapshot? session)
    {
        if (session is not null && session.State == SessionState.Active)
        {
            return "Active";
        }

        if (session is not null && session.State == SessionState.Recovering)
        {
            return "Recovering";
        }

        if (session is not null && session.State == SessionState.Failed)
        {
            return "Failed";
        }

        if (connector is not null)
        {
            return "Registered";
        }

        return "Unknown";
    }

    private static string FormatMetricValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            JsonElement element => element.ToString(),
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
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var parsedNumber) => parsedNumber,
            JsonElement element when element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out var parsedTextNumber) => parsedTextNumber,
            string text when double.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private async Task<HashSet<string>?> GetVisibleSessionIdsAsync(ApiCallerContext? caller, CancellationToken cancellationToken)
    {
        if (caller is null || !caller.IsPresent)
        {
            return null;
        }

        return FilterVisibleSessions(await _sessionStore.ListAsync(cancellationToken), caller)
            .Select(snapshot => snapshot.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<SessionSnapshot> FilterVisibleSessions(IEnumerable<SessionSnapshot> sessions, ApiCallerContext? caller)
    {
        if (caller is null || !caller.IsPresent)
        {
            return sessions;
        }

        return sessions.Where(caller.CanAccessSession);
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
