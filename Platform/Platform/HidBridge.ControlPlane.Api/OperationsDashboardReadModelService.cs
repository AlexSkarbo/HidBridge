using HidBridge.Abstractions;
using HidBridge.Contracts;
using System.Text.Json;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Builds fleet-oriented dashboard projections for inventory, audit, and telemetry views.
/// </summary>
public sealed class OperationsDashboardReadModelService
{
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly IEndpointSnapshotStore _endpointStore;
    private readonly ISessionStore _sessionStore;
    private readonly IEventStore _eventStore;

    /// <summary>
    /// Creates the operations dashboard service.
    /// </summary>
    /// <param name="connectorRegistry">Provides registered connectors and agent descriptors.</param>
    /// <param name="endpointStore">Provides persisted endpoint snapshots.</param>
    /// <param name="sessionStore">Provides persisted session snapshots.</param>
    /// <param name="eventStore">Provides persisted audit and telemetry events.</param>
    public OperationsDashboardReadModelService(
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
    /// Builds the fleet inventory dashboard.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The aggregated fleet inventory read model.</returns>
    public async Task<InventoryDashboardReadModel> GetInventoryDashboardAsync(ApiCallerContext? caller, CancellationToken cancellationToken)
    {
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var connectors = await _connectorRegistry.ListAsync(cancellationToken);
        var endpoints = await _endpointStore.ListAsync(cancellationToken);
        var sessions = FilterVisibleSessions(await _sessionStore.ListAsync(cancellationToken), caller).ToArray();

        var connectorByEndpoint = connectors
            .GroupBy(x => x.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var latestSessionByEndpoint = sessions
            .GroupBy(x => x.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x
                    .OrderByDescending(session => session.UpdatedAtUtc)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        var visibleEndpointIds = sessions.Select(x => x.EndpointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredEndpoints = caller is { IsPresent: true, IsAdmin: false, HasScope: true }
            ? endpoints.Where(endpoint => visibleEndpointIds.Contains(endpoint.EndpointId)).ToArray()
            : endpoints;

        var endpointCards = filteredEndpoints
            .OrderBy(x => x.EndpointId, StringComparer.OrdinalIgnoreCase)
            .Select(endpoint =>
            {
                connectorByEndpoint.TryGetValue(endpoint.EndpointId, out var connector);
                latestSessionByEndpoint.TryGetValue(endpoint.EndpointId, out var activeSession);
                var status = ResolveEndpointStatus(connector, activeSession);

                return new InventoryEndpointCardReadModel(
                    EndpointId: endpoint.EndpointId,
                    AgentId: connector?.AgentId,
                    ConnectorType: connector?.ConnectorType,
                    Status: status,
                    CapabilityCount: endpoint.Capabilities.Count,
                    CapabilityNames: endpoint.Capabilities
                        .Select(capability => capability.Name)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    ActiveSessionId: activeSession?.SessionId,
                    ActiveSessionState: activeSession?.State,
                    ActiveControllerPrincipalId: activeSession?.ControlLease?.PrincipalId,
                    UpdatedAtUtc: endpoint.UpdatedAtUtc);
            })
            .ToArray();

        var filteredConnectors = caller is { IsPresent: true, IsAdmin: false, HasScope: true }
            ? connectors.Where(connector => filteredEndpoints.Any(endpoint => string.Equals(endpoint.EndpointId, connector.EndpointId, StringComparison.OrdinalIgnoreCase))).ToArray()
            : connectors;

        var connectorTypeSummary = filteredConnectors
            .GroupBy(x => x.ConnectorType)
            .Select(group => new ConnectorTypeSummaryReadModel(group.Key, group.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.ConnectorType.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new InventoryDashboardReadModel(
            GeneratedAtUtc: generatedAtUtc,
            TotalAgents: filteredConnectors.Count,
            TotalEndpoints: filteredEndpoints.Count,
            TotalSessions: sessions.Count(),
            ActiveSessions: sessions.Count(x => x.State == SessionState.Active),
            RecoveringSessions: sessions.Count(x => x.State == SessionState.Recovering),
            FailedSessions: sessions.Count(x => x.State == SessionState.Failed),
            ActiveControlLeases: sessions.Count(x => x.ControlLease is not null),
            ConnectorTypes: connectorTypeSummary,
            Endpoints: endpointCards);
    }

    /// <summary>
    /// Builds the audit dashboard.
    /// </summary>
    /// <param name="take">Maximum number of recent entries to include.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The aggregated audit dashboard read model.</returns>
    public async Task<AuditDashboardReadModel> GetAuditDashboardAsync(ApiCallerContext? caller, int take, CancellationToken cancellationToken)
    {
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var normalizedTake = Math.Clamp(take, 1, 200);
        var visibleSessionIds = await GetVisibleSessionIdsAsync(caller, cancellationToken);
        var events = (await _eventStore.ListAuditAsync(cancellationToken))
            .Where(x => IsVisibleSessionBoundRecord(x.SessionId, visibleSessionIds, caller))
            .ToArray();

        var categories = events
            .GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditCategorySummaryReadModel(group.Key, group.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sessions = events
            .Where(x => !string.IsNullOrWhiteSpace(x.SessionId))
            .GroupBy(x => x.SessionId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SessionAuditSummaryReadModel(
                group.Key,
                group.Count(),
                group
                    .Select(x => x.CreatedAtUtc)
                    .Where(x => x.HasValue)
                    .Select(x => x!.Value)
                    .OrderByDescending(x => x)
                    .FirstOrDefault()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var recentEntries = events
            .OrderByDescending(x => x.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .Take(normalizedTake)
            .Select(x => new AuditRecentEntryReadModel(
                x.Category,
                x.Message,
                x.SessionId,
                x.CreatedAtUtc ?? generatedAtUtc))
            .ToArray();

        return new AuditDashboardReadModel(
            GeneratedAtUtc: generatedAtUtc,
            TotalEvents: events.Count(),
            SessionScopedEvents: events.Count(x => !string.IsNullOrWhiteSpace(x.SessionId)),
            GlobalEvents: events.Count(x => string.IsNullOrWhiteSpace(x.SessionId)),
            LatestEventAtUtc: events
                .Select(x => x.CreatedAtUtc)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .OrderByDescending(x => x)
                .FirstOrDefault(),
            Categories: categories,
            Sessions: sessions,
            RecentEntries: recentEntries);
    }

    /// <summary>
    /// Builds the telemetry dashboard.
    /// </summary>
    /// <param name="take">Maximum number of latest signals to include.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The aggregated telemetry dashboard read model.</returns>
    public async Task<TelemetryDashboardReadModel> GetTelemetryDashboardAsync(ApiCallerContext? caller, int take, CancellationToken cancellationToken)
    {
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var normalizedTake = Math.Clamp(take, 1, 200);
        var visibleSessionIds = await GetVisibleSessionIdsAsync(caller, cancellationToken);
        var events = (await _eventStore.ListTelemetryAsync(cancellationToken))
            .Where(x => IsVisibleSessionBoundRecord(x.SessionId, visibleSessionIds, caller))
            .ToArray();

        var scopes = events
            .GroupBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TelemetryScopeSummaryReadModel(
                group.Key,
                group.Count(),
                group
                    .Select(x => x.CreatedAtUtc)
                    .Where(x => x.HasValue)
                    .Select(x => x!.Value)
                    .OrderByDescending(x => x)
                    .FirstOrDefault()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var signals = events
            .SelectMany(@event => @event.Metrics.Select(metric => new
            {
                @event.Scope,
                MetricName = metric.Key,
                MetricValue = metric.Value,
                OccurredAtUtc = @event.CreatedAtUtc ?? generatedAtUtc,
            }))
            .GroupBy(x => $"{x.Scope}:{x.MetricName}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latest = group.OrderByDescending(x => x.OccurredAtUtc).First();
                return new TelemetrySignalReadModel(
                    latest.Scope,
                    latest.MetricName,
                    LatestValue: FormatMetricValue(latest.MetricValue),
                    LatestNumericValue: TryConvertMetricValue(latest.MetricValue),
                    LatestAtUtc: latest.OccurredAtUtc);
            })
            .OrderByDescending(x => x.LatestAtUtc)
            .ThenBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.MetricName, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedTake)
            .ToArray();

        var sessionHealth = events
            .Where(x => !string.IsNullOrWhiteSpace(x.SessionId))
            .GroupBy(x => x.SessionId!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var latestEvent = group
                    .OrderByDescending(x => x.CreatedAtUtc ?? DateTimeOffset.MinValue)
                    .First();

                var latestMetrics = latestEvent.Metrics
                    .OrderBy(metric => metric.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        metric => metric.Key,
                        metric => FormatMetricValue(metric.Value),
                        StringComparer.OrdinalIgnoreCase);

                return new TelemetrySessionHealthReadModel(
                    SessionId: group.Key,
                    EventCount: group.Count(),
                    LatestEventAtUtc: latestEvent.CreatedAtUtc,
                    LatestMetrics: latestMetrics);
            })
            .OrderByDescending(x => x.LatestEventAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedTake)
            .ToArray();

        return new TelemetryDashboardReadModel(
            GeneratedAtUtc: generatedAtUtc,
            TotalEvents: events.Count(),
            SessionScopedEvents: events.Count(x => !string.IsNullOrWhiteSpace(x.SessionId)),
            LatestEventAtUtc: events
                .Select(x => x.CreatedAtUtc)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .OrderByDescending(x => x)
                .FirstOrDefault(),
            Scopes: scopes,
            Signals: signals,
            Sessions: sessionHealth);
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
