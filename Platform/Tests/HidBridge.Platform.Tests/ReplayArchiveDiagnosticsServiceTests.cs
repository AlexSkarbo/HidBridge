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
/// Verifies replay-oriented and archive-oriented diagnostics read models.
/// </summary>
public sealed class ReplayArchiveDiagnosticsServiceTests
{
    private sealed class FakeConnector : IConnector
    {
        public FakeConnector(string agentId, string endpointId)
        {
            Descriptor = new ConnectorDescriptor(
                agentId,
                endpointId,
                ConnectorType.HidBridge,
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
    /// Verifies that one replay bundle contains session, audit, telemetry, command, and timeline data.
    /// </summary>
    [Fact]
    public async Task GetSessionReplayBundleAsync_ReturnsCombinedReplayData()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, orchestrator, eventStore, journalStore) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("replay-session", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);
            await eventStore.AppendAuditAsync(
                new AuditEventBody("session", "opened", "replay-session", null, DateTimeOffset.UtcNow.AddMinutes(-2)),
                TestContext.Current.CancellationToken);
            await eventStore.AppendTelemetryAsync(
                new TelemetryEventBody("stream", new Dictionary<string, object?> { ["fps"] = 25 }, "replay-session", DateTimeOffset.UtcNow.AddMinutes(-1)),
                TestContext.Current.CancellationToken);
            await journalStore.AppendAsync(
                new CommandJournalEntryBody("cmd-1", "replay-session", "agent-1", CommandChannel.Hid, "keyboard.text", new Dictionary<string, object?>(), 250, "idem-1", CommandStatus.Applied, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

            var bundle = await service.GetSessionReplayBundleAsync("replay-session", null, 20, TestContext.Current.CancellationToken);

            Assert.Equal("replay-session", bundle.SessionId);
            Assert.Equal(2, bundle.AuditCount);
            Assert.Equal(1, bundle.TelemetryCount);
            Assert.Equal(1, bundle.CommandCount);
            Assert.NotEmpty(bundle.Timeline);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that archive summary aggregates the persisted diagnostic counts.
    /// </summary>
    [Fact]
    public async Task GetArchiveSummaryAsync_ReturnsAggregateCounts()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, orchestrator, eventStore, journalStore) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("archive-session", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);
            await eventStore.AppendAuditAsync(
                new AuditEventBody("session", "opened", "archive-session", null, DateTimeOffset.UtcNow.AddMinutes(-2)),
                TestContext.Current.CancellationToken);
            await eventStore.AppendTelemetryAsync(
                new TelemetryEventBody("command", new Dictionary<string, object?> { ["status"] = "Applied" }, "archive-session", DateTimeOffset.UtcNow.AddMinutes(-1)),
                TestContext.Current.CancellationToken);
            await journalStore.AppendAsync(
                new CommandJournalEntryBody("cmd-archive", "archive-session", "agent-1", CommandChannel.Hid, "keyboard.text", new Dictionary<string, object?>(), 250, "idem-archive", CommandStatus.Applied, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

            var summary = await service.GetArchiveSummaryAsync("archive-session", null, null, TestContext.Current.CancellationToken);

            Assert.Equal("archive-session", summary.SessionId);
            Assert.Equal(2, summary.AuditCount);
            Assert.Equal(1, summary.TelemetryCount);
            Assert.Equal(1, summary.CommandCount);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that archive slices support filtering.
    /// </summary>
    [Fact]
    public async Task ArchiveQueries_FilterBySessionScopeAndStatus()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, orchestrator, eventStore, journalStore) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("filter-session", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);
            await eventStore.AppendAuditAsync(
                new AuditEventBody("session.control", "requested", "filter-session", new Dictionary<string, object?> { ["principalId"] = "operator-a" }, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            await eventStore.AppendTelemetryAsync(
                new TelemetryEventBody("command", new Dictionary<string, object?> { ["status"] = "Rejected" }, "filter-session", DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            await journalStore.AppendAsync(
                new CommandJournalEntryBody("cmd-filter", "filter-session", "agent-1", CommandChannel.Hid, "noop", new Dictionary<string, object?>(), 250, "idem-filter", CommandStatus.Rejected, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

            var audit = await service.QueryArchiveAuditAsync("filter-session", "session.control", "operator-a", null, null, 0, 20, TestContext.Current.CancellationToken);
            var telemetry = await service.QueryArchiveTelemetryAsync("filter-session", "command", "status", null, null, 0, 20, TestContext.Current.CancellationToken);
            var commands = await service.QueryArchiveCommandsAsync("filter-session", CommandStatus.Rejected, null, null, 0, 20, TestContext.Current.CancellationToken);

            Assert.Equal(1, audit.Total);
            Assert.Equal(1, telemetry.Total);
            Assert.Equal(1, commands.Total);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// Verifies that archive diagnostics reject access to sessions outside of the caller tenant and organization scope.
    /// </summary>
    [Fact]
    public async Task GetSessionReplayBundleAsync_RejectsForeignCallerScope()
    {
        var rootDirectory = CreateRootDirectory();
        try
        {
            var (service, orchestrator, _, _) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("scope-session", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1", SessionRole.Owner, "tenant-a", "org-a"),
                TestContext.Current.CancellationToken);

            var caller = new ApiCallerContext("user-1", "viewer@example.com", "tenant-b", "org-a", ["operator.viewer"]);

            var exception = await Assert.ThrowsAsync<ApiAuthorizationException>(() =>
                service.GetSessionReplayBundleAsync("scope-session", caller, 20, TestContext.Current.CancellationToken));

            Assert.Equal("tenant_scope_mismatch", exception.Code);
        }
        finally
        {
            DeleteRootDirectory(rootDirectory);
        }
    }

    private static string CreateRootDirectory()
        => Path.Combine(Path.GetTempPath(), "hidbridge-replay-archive-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRootDirectory(string rootDirectory)
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static async Task<(ReplayArchiveDiagnosticsService Service, InMemorySessionOrchestrator Orchestrator, FileEventStore EventStore, FileCommandJournalStore JournalStore)> CreateHarnessAsync(string rootDirectory)
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

        var service = new ReplayArchiveDiagnosticsService(sessionStore, eventStore, commandJournalStore);
        return (service, orchestrator, eventStore, commandJournalStore);
    }
}
