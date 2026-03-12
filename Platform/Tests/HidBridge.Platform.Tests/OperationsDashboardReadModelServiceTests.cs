using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.ConnectorHost;
using HidBridge.Contracts;
using HidBridge.ControlPlane.Api;
using HidBridge.Persistence;
using HidBridge.SessionOrchestrator;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies fleet, audit, and telemetry dashboard projections.
/// </summary>
public sealed class OperationsDashboardReadModelServiceTests
{
    private sealed class FakeConnector : IConnector
    {
        public FakeConnector(string agentId, string endpointId, ConnectorType connectorType = ConnectorType.HidBridge)
        {
            Descriptor = new ConnectorDescriptor(
                agentId,
                endpointId,
                connectorType,
                new[]
                {
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.DiagnosticsTelemetryV1, "1.0"),
                });
        }

        public ConnectorDescriptor Descriptor { get; }

        public Task<AgentRegisterBody> RegisterAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentRegisterBody(Descriptor.ConnectorType, "test", Descriptor.Capabilities));

        public Task<AgentHeartbeatBody> HeartbeatAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentHeartbeatBody(AgentStatus.Online));

        public Task<ConnectorCommandResult> ExecuteAsync(CommandRequestBody command, CancellationToken cancellationToken)
            => Task.FromResult(new ConnectorCommandResult(command.CommandId, CommandStatus.Applied));
    }

    /// <summary>
    /// Verifies that the inventory dashboard aggregates fleet cards and connector buckets.
    /// </summary>
    [Fact]
    public async Task GetInventoryDashboardAsync_AggregatesFleetState()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, orchestrator, registry, _) = await CreateHarnessAsync(rootDirectory);

            await registry.RegisterAsync(new FakeConnector("agent-2", "endpoint-2", ConnectorType.Agentless), TestContext.Current.CancellationToken);
            await registry.UpsertEndpointAsync(
                "endpoint-2",
                new[]
                {
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.DiagnosticsTelemetryV1, "1.0"),
                },
                TestContext.Current.CancellationToken);
            await orchestrator.OpenAsync(
                new SessionOpenBody("inventory-session", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);
            await orchestrator.GrantControlAsync(
                "inventory-session",
                new SessionControlGrantBody("owner:owner", "owner", 30, "baseline control"),
                TestContext.Current.CancellationToken);

            var dashboard = await service.GetInventoryDashboardAsync(null, TestContext.Current.CancellationToken);

            Assert.Equal(2, dashboard.TotalAgents);
            Assert.Equal(2, dashboard.TotalEndpoints);
            Assert.Equal(1, dashboard.ActiveSessions);
            Assert.Equal(1, dashboard.ActiveControlLeases);
            Assert.Contains(dashboard.ConnectorTypes, x => x.ConnectorType == ConnectorType.HidBridge && x.Count == 1);
            Assert.Contains(dashboard.ConnectorTypes, x => x.ConnectorType == ConnectorType.Agentless && x.Count == 1);
            Assert.Contains(dashboard.Endpoints, x => x.EndpointId == "endpoint-1" && x.Status == "Active" && x.ActiveControllerPrincipalId == "owner");
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that the audit dashboard groups category totals and recent entries.
    /// </summary>
    [Fact]
    public async Task GetAuditDashboardAsync_GroupsAuditMetrics()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, _, _, eventStore) = await CreateHarnessAsync(rootDirectory);

            await eventStore.AppendAuditAsync(
                new AuditEventBody("session", "Session opened", "audit-session", new Dictionary<string, object?> { ["principalId"] = "owner" }, DateTimeOffset.UtcNow.AddMinutes(-2)),
                TestContext.Current.CancellationToken);
            await eventStore.AppendAuditAsync(
                new AuditEventBody("control", "Control granted", "audit-session", new Dictionary<string, object?> { ["principalId"] = "controller" }, DateTimeOffset.UtcNow.AddMinutes(-1)),
                TestContext.Current.CancellationToken);
            await eventStore.AppendAuditAsync(
                new AuditEventBody("system", "Connector reconciled", null, null, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

            var dashboard = await service.GetAuditDashboardAsync(null, 10, TestContext.Current.CancellationToken);

            Assert.Equal(3, dashboard.TotalEvents);
            Assert.Contains(dashboard.Categories, x => x.Category == "system");
            Assert.Contains(dashboard.Categories, x => x.Category == "session");
            Assert.Contains(dashboard.Categories, x => x.Category == "control");
            Assert.Contains(dashboard.Sessions, x => x.SessionId == "audit-session" && x.Count == 2);
            Assert.NotEmpty(dashboard.RecentEntries);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that the telemetry dashboard produces scope, signal, and session-health projections.
    /// </summary>
    [Fact]
    public async Task GetTelemetryDashboardAsync_GroupsTelemetryMetrics()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, _, _, eventStore) = await CreateHarnessAsync(rootDirectory);

            await eventStore.AppendTelemetryAsync(
                new TelemetryEventBody("stream", new Dictionary<string, object?> { ["fps"] = 24, ["latencyMs"] = 82 }, "telemetry-session", DateTimeOffset.UtcNow.AddMinutes(-2)),
                TestContext.Current.CancellationToken);
            await eventStore.AppendTelemetryAsync(
                new TelemetryEventBody("stream", new Dictionary<string, object?> { ["fps"] = 25, ["latencyMs"] = 75 }, "telemetry-session", DateTimeOffset.UtcNow.AddMinutes(-1)),
                TestContext.Current.CancellationToken);
            await eventStore.AppendTelemetryAsync(
                new TelemetryEventBody("connector", new Dictionary<string, object?> { ["queueDepth"] = 1 }, null, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

            var dashboard = await service.GetTelemetryDashboardAsync(null, 20, TestContext.Current.CancellationToken);

            Assert.Equal(3, dashboard.TotalEvents);
            Assert.Contains(dashboard.Scopes, x => x.Scope == "stream" && x.Count == 2);
            Assert.Contains(dashboard.Signals, x => x.Scope == "stream" && x.MetricName == "fps" && x.LatestNumericValue == 25);
            Assert.Contains(dashboard.Sessions, x => x.SessionId == "telemetry-session" && x.EventCount == 2);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that fleet dashboards are filtered to sessions visible in the caller tenant and organization scope.
    /// </summary>
    [Fact]
    public async Task GetInventoryDashboardAsync_FiltersByCallerScope()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, orchestrator, registry, _) = await CreateHarnessAsync(rootDirectory);

            await registry.RegisterAsync(new FakeConnector("agent-2", "endpoint-2", ConnectorType.Agentless), TestContext.Current.CancellationToken);
            await registry.UpsertEndpointAsync(
                "endpoint-2",
                new[]
                {
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.DiagnosticsTelemetryV1, "1.0"),
                },
                TestContext.Current.CancellationToken);
            await orchestrator.OpenAsync(
                new SessionOpenBody("inventory-tenant-a", SessionProfile.UltraLowLatency, "owner-a", "agent-1", "endpoint-1", SessionRole.Owner, "tenant-a", "org-a"),
                TestContext.Current.CancellationToken);
            await orchestrator.OpenAsync(
                new SessionOpenBody("inventory-tenant-b", SessionProfile.UltraLowLatency, "owner-b", "agent-2", "endpoint-2", SessionRole.Owner, "tenant-b", "org-b"),
                TestContext.Current.CancellationToken);

            var caller = new ApiCallerContext("user-1", "viewer@example.com", "tenant-a", "org-a", ["operator.viewer"]);
            var dashboard = await service.GetInventoryDashboardAsync(caller, TestContext.Current.CancellationToken);

            Assert.Equal(1, dashboard.TotalAgents);
            Assert.Equal(1, dashboard.TotalEndpoints);
            Assert.Equal(1, dashboard.TotalSessions);
            Assert.Single(dashboard.Endpoints);
            Assert.Equal("endpoint-1", dashboard.Endpoints[0].EndpointId);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    private static string CreateRootDirectory()
        => Path.Combine(Path.GetTempPath(), "hidbridge-operations-dashboard-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRootDirectory(string rootDirectory)
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static async Task<(OperationsDashboardReadModelService Service, InMemorySessionOrchestrator Orchestrator, InMemoryConnectorRegistry Registry, FileEventStore EventStore)> CreateHarnessAsync(string rootDirectory)
    {
        var options = new FilePersistenceOptions(rootDirectory);
        var eventStore = new FileEventStore(options);
        var commandJournalStore = new FileCommandJournalStore(options);
        var sessionStore = new FileSessionStore(options);
        var endpointStore = new FileEndpointSnapshotStore(options);
        var registry = new InMemoryConnectorRegistry(
            new FileConnectorCatalogStore(options),
            endpointStore,
            eventStore);

        await registry.RegisterAsync(new FakeConnector("agent-1", "endpoint-1"), TestContext.Current.CancellationToken);
        await registry.UpsertEndpointAsync(
            "endpoint-1",
            new[]
            {
                new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                new CapabilityDescriptor(CapabilityNames.DiagnosticsTelemetryV1, "1.0"),
            },
            TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry,
            commandJournalStore,
            sessionStore);

        var service = new OperationsDashboardReadModelService(registry, endpointStore, sessionStore, eventStore);
        return (service, orchestrator, registry, eventStore);
    }
}
