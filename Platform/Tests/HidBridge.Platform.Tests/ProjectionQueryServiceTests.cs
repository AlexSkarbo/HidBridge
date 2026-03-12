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
/// Verifies optimized projection queries used by operator-oriented API endpoints.
/// </summary>
public sealed class ProjectionQueryServiceTests
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
    /// Verifies that session projections support state and principal filtering.
    /// </summary>
    [Fact]
    public async Task QuerySessionsAsync_FiltersByStateAndPrincipal()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, orchestrator, _, _) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("session-active", SessionProfile.UltraLowLatency, "owner-a", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);
            await orchestrator.OpenAsync(
                new SessionOpenBody("session-closed", SessionProfile.Balanced, "owner-b", "agent-2", "endpoint-2"),
                TestContext.Current.CancellationToken);
            await orchestrator.CloseAsync(
                new SessionCloseBody("session-closed", "done"),
                TestContext.Current.CancellationToken);

            var projection = await service.QuerySessionsAsync(
                SessionState.Active,
                null,
                null,
                "owner-a",
                null,
                0,
                20,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, projection.Total);
            Assert.Single(projection.Items);
            Assert.Equal("session-active", projection.Items[0].SessionId);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that endpoint projections support status and connector filters.
    /// </summary>
    [Fact]
    public async Task QueryEndpointsAsync_FiltersByStatusAndConnectorType()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, orchestrator, _, _) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("endpoint-session", SessionProfile.UltraLowLatency, "owner-a", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            var projection = await service.QueryEndpointsAsync(
                "Active",
                null,
                ConnectorType.HidBridge,
                true,
                null,
                0,
                20,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, projection.Total);
            Assert.Single(projection.Items);
            Assert.Equal("endpoint-1", projection.Items[0].EndpointId);
            Assert.Equal("Active", projection.Items[0].Status);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that audit projections support category and principal filters.
    /// </summary>
    [Fact]
    public async Task QueryAuditAsync_FiltersByCategoryAndPrincipal()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, _, _, eventStore) = await CreateHarnessAsync(rootDirectory);

            await eventStore.AppendAuditAsync(
                new AuditEventBody("session.control", "taken", "audit-1", new Dictionary<string, object?> { ["principalId"] = "controller-a" }, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            await eventStore.AppendAuditAsync(
                new AuditEventBody("session.share", "granted", "audit-1", new Dictionary<string, object?> { ["principalId"] = "observer-b" }, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

            var projection = await service.QueryAuditAsync(
                "session.control",
                null,
                "controller-a",
                null,
                null,
                0,
                20,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, projection.Total);
            Assert.Single(projection.Items);
            Assert.Equal("session.control", projection.Items[0].Category);
            Assert.Equal("controller-a", projection.Items[0].PrincipalId);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that telemetry projections support scope, session, and metric filters.
    /// </summary>
    [Fact]
    public async Task QueryTelemetryAsync_FiltersByScopeSessionAndMetric()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, _, _, eventStore) = await CreateHarnessAsync(rootDirectory);

            await eventStore.AppendTelemetryAsync(
                new TelemetryEventBody("stream", new Dictionary<string, object?> { ["fps"] = 25, ["latencyMs"] = 75 }, "telemetry-1", DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            await eventStore.AppendTelemetryAsync(
                new TelemetryEventBody("connector", new Dictionary<string, object?> { ["queueDepth"] = 1 }, null, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

            var projection = await service.QueryTelemetryAsync(
                "stream",
                "telemetry-1",
                "fps",
                null,
                null,
                0,
                20,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, projection.Total);
            Assert.Single(projection.Items);
            Assert.Equal("stream", projection.Items[0].Scope);
            Assert.Equal("telemetry-1", projection.Items[0].SessionId);
            Assert.Equal(25, projection.Items[0].NumericMetrics["fps"]);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that session projections are filtered by caller tenant and organization scope.
    /// </summary>
    [Fact]
    public async Task QuerySessionsAsync_FiltersByCallerScope()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, orchestrator, _, _) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("session-tenant-a", SessionProfile.UltraLowLatency, "owner-a", "agent-1", "endpoint-1", SessionRole.Owner, "tenant-a", "org-a"),
                TestContext.Current.CancellationToken);
            await orchestrator.OpenAsync(
                new SessionOpenBody("session-tenant-b", SessionProfile.UltraLowLatency, "owner-b", "agent-2", "endpoint-2", SessionRole.Owner, "tenant-b", "org-b"),
                TestContext.Current.CancellationToken);

            var caller = new ApiCallerContext("user-1", "viewer@example.com", "tenant-a", "org-a", ["operator.viewer"]);
            var projection = await service.QuerySessionsAsync(
                null,
                null,
                null,
                null,
                caller,
                0,
                20,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, projection.Total);
            Assert.Single(projection.Items);
            Assert.Equal("session-tenant-a", projection.Items[0].SessionId);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    private static string CreateRootDirectory()
        => Path.Combine(Path.GetTempPath(), "hidbridge-projection-query-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRootDirectory(string rootDirectory)
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static async Task<(ProjectionQueryService Service, InMemorySessionOrchestrator Orchestrator, InMemoryConnectorRegistry Registry, FileEventStore EventStore)> CreateHarnessAsync(string rootDirectory)
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
        await registry.RegisterAsync(new FakeConnector("agent-2", "endpoint-2", ConnectorType.Agentless), TestContext.Current.CancellationToken);
        await registry.UpsertEndpointAsync(
            "endpoint-1",
            new[]
            {
                new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                new CapabilityDescriptor(CapabilityNames.DiagnosticsTelemetryV1, "1.0"),
            },
            TestContext.Current.CancellationToken);
        await registry.UpsertEndpointAsync(
            "endpoint-2",
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

        var service = new ProjectionQueryService(registry, endpointStore, sessionStore, eventStore);
        return (service, orchestrator, registry, eventStore);
    }
}
