using HidBridge.Application;
using HidBridge.Abstractions;
using HidBridge.ConnectorHost;
using HidBridge.Contracts;
using HidBridge.ControlPlane.Api;
using HidBridge.Persistence;
using HidBridge.SessionOrchestrator;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies collaboration dashboard and analytics read models.
/// </summary>
public sealed class CollaborationReadModelServiceTests
{
    private sealed class FakeConnector : IConnector
    {
        public FakeConnector(string agentId, string endpointId)
        {
            Descriptor = new ConnectorDescriptor(
                agentId,
                endpointId,
                ConnectorType.HidBridge,
                new[] { new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0") });
        }

        public ConnectorDescriptor Descriptor { get; }

        public Task<AgentRegisterBody> RegisterAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentRegisterBody(Descriptor.ConnectorType, "test", Descriptor.Capabilities));

        public Task<AgentHeartbeatBody> HeartbeatAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentHeartbeatBody(AgentStatus.Online));

        public Task<ConnectorCommandResult> ExecuteAsync(CommandRequestBody command, CancellationToken cancellationToken)
            => Task.FromResult(new ConnectorCommandResult(command.CommandId, CommandStatus.Applied));
    }

    [Fact]
    public async Task GetSessionDashboardAsync_UsesExplicitStateTransitionReasonAndTimestamp()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-read-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FilePersistenceOptions(rootDirectory);
            var eventStore = new FileEventStore(options);
            var commandJournalStore = new FileCommandJournalStore(options);
            var sessionStore = new FileSessionStore(options);
            var nowUtc = DateTimeOffset.UtcNow;
            var transitionAtUtc = nowUtc.AddSeconds(-30);

            await sessionStore.UpsertAsync(
                new SessionSnapshot(
                    SessionId: "reason-session",
                    AgentId: "agent-1",
                    EndpointId: "endpoint-1",
                    Profile: SessionProfile.Balanced,
                    RequestedBy: "owner",
                    Role: SessionRole.Owner,
                    State: SessionState.Failed,
                    UpdatedAtUtc: nowUtc,
                    Participants:
                    [
                        new SessionParticipantSnapshot("owner:owner", "owner", SessionRole.Owner, nowUtc, nowUtc),
                    ]),
                TestContext.Current.CancellationToken);

            await eventStore.AppendAuditAsync(
                new AuditEventBody(
                    Category: "session.reconcile",
                    Message: "Session reason-session moved from Recovering to Failed",
                    SessionId: "reason-session",
                    Data: new Dictionary<string, object?>
                    {
                        ["previousState"] = "Recovering",
                        ["nextState"] = "Failed",
                        ["reason"] = "lease_recovery_timeout",
                    },
                    CreatedAtUtc: transitionAtUtc),
                TestContext.Current.CancellationToken);

            var service = new CollaborationReadModelService(sessionStore, eventStore, commandJournalStore);
            var dashboard = await service.GetSessionDashboardAsync("reason-session", TestContext.Current.CancellationToken);

            Assert.Equal("lease_recovery_timeout", dashboard.StateReason);
            Assert.Equal(transitionAtUtc, dashboard.StateChangedAtUtc);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetSessionDashboardAsync_FallsBackToLatestCommandFailureWhenTransitionEventIsMissing()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-read-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FilePersistenceOptions(rootDirectory);
            var eventStore = new FileEventStore(options);
            var commandJournalStore = new FileCommandJournalStore(options);
            var sessionStore = new FileSessionStore(options);
            var nowUtc = DateTimeOffset.UtcNow;
            var failureAtUtc = nowUtc.AddSeconds(-5);

            await sessionStore.UpsertAsync(
                new SessionSnapshot(
                    SessionId: "reason-fallback-session",
                    AgentId: "agent-1",
                    EndpointId: "endpoint-1",
                    Profile: SessionProfile.Balanced,
                    RequestedBy: "owner",
                    Role: SessionRole.Owner,
                    State: SessionState.Recovering,
                    UpdatedAtUtc: nowUtc,
                    Participants:
                    [
                        new SessionParticipantSnapshot("owner:owner", "owner", SessionRole.Owner, nowUtc, nowUtc),
                    ]),
                TestContext.Current.CancellationToken);

            await commandJournalStore.AppendAsync(
                new CommandJournalEntryBody(
                    CommandId: "cmd-timeout-1",
                    SessionId: "reason-fallback-session",
                    AgentId: "agent-1",
                    Channel: CommandChannel.Hid,
                    Action: "keyboard.text",
                    Args: new Dictionary<string, object?>(),
                    TimeoutMs: 250,
                    IdempotencyKey: "cmd-timeout-1",
                    Status: CommandStatus.Timeout,
                    CreatedAtUtc: failureAtUtc,
                    CompletedAtUtc: failureAtUtc),
                TestContext.Current.CancellationToken);

            var service = new CollaborationReadModelService(sessionStore, eventStore, commandJournalStore);
            var dashboard = await service.GetSessionDashboardAsync("reason-fallback-session", TestContext.Current.CancellationToken);

            Assert.Equal("command_timeout", dashboard.StateReason);
            Assert.Equal(failureAtUtc, dashboard.StateChangedAtUtc);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that the session dashboard aggregates participant, share, command, and timeline counts.
    /// </summary>
    [Fact]
    public async Task GetSessionDashboardAsync_AggregatesSessionMetrics()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-read-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (service, orchestrator, eventStore) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("dashboard-session", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            await orchestrator.UpsertParticipantAsync(
                "dashboard-session",
                new SessionParticipantUpsertBody("observer-1", "observer-user", SessionRole.Observer, "owner"),
                TestContext.Current.CancellationToken);

            await orchestrator.GrantShareAsync(
                "dashboard-session",
                new SessionShareGrantBody("share-1", "controller-user", "owner", SessionRole.Controller),
                TestContext.Current.CancellationToken);

            await eventStore.AppendTelemetryAsync(
                new TelemetryEventBody("stream", new Dictionary<string, object?> { ["fps"] = 24 }, "dashboard-session"),
                TestContext.Current.CancellationToken);

            await orchestrator.DispatchAsync(
                new CommandRequestBody(
                    "cmd-dashboard-1",
                    "dashboard-session",
                    CommandChannel.Hid,
                    "keyboard.text",
                    new Dictionary<string, object?> { ["text"] = "hello", ["principalId"] = "owner", ["participantId"] = "owner:owner" },
                    250,
                    "cmd-dashboard-1"),
                TestContext.Current.CancellationToken);

            var dashboard = await service.GetSessionDashboardAsync("dashboard-session", TestContext.Current.CancellationToken);

            Assert.Equal("dashboard-session", dashboard.SessionId);
            Assert.Equal(2, dashboard.ParticipantCount);
            Assert.Equal(1, dashboard.Observers);
            Assert.Equal(1, dashboard.PendingShares);
            Assert.Equal(1, dashboard.CommandCount);
            Assert.True(dashboard.TimelineEntries >= 2);
            Assert.NotNull(dashboard.LastActivityAtUtc);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that participant activity and operator timeline projections use command metadata correctly.
    /// </summary>
    [Fact]
    public async Task ParticipantActivityAndOperatorTimeline_FilterByPrincipal()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-read-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (service, orchestrator, eventStore) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("activity-session", SessionProfile.Balanced, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            await orchestrator.GrantShareAsync(
                "activity-session",
                new SessionShareGrantBody("share-operator", "operator-user", "owner", SessionRole.Controller),
                TestContext.Current.CancellationToken);

            await orchestrator.AcceptShareAsync(
                "activity-session",
                new SessionShareTransitionBody("share-operator", "operator-user"),
                TestContext.Current.CancellationToken);

            await orchestrator.GrantControlAsync(
                "activity-session",
                new SessionControlGrantBody("share:share-operator", "owner", 30, "operator takeover"),
                TestContext.Current.CancellationToken);

            await eventStore.AppendAuditAsync(
                new AuditEventBody("session", "Operator joined", "activity-session", new Dictionary<string, object?> { ["principalId"] = "operator-user" }),
                TestContext.Current.CancellationToken);

            await orchestrator.DispatchAsync(
                new CommandRequestBody(
                    "cmd-activity-1",
                    "activity-session",
                    CommandChannel.Hid,
                    "mouse.move",
                    new Dictionary<string, object?> { ["dx"] = 4, ["dy"] = 2, ["principalId"] = "operator-user", ["participantId"] = "share:share-operator", ["shareId"] = "share-operator" },
                    250,
                    "cmd-activity-1"),
                TestContext.Current.CancellationToken);

            var activity = await service.GetParticipantActivityAsync("activity-session", TestContext.Current.CancellationToken);
            var operatorActivity = Assert.Single(activity, x => x.PrincipalId == "operator-user");
            var timeline = await service.GetOperatorTimelineAsync("activity-session", "operator-user", 20, TestContext.Current.CancellationToken);

            Assert.Equal(1, operatorActivity.CommandCount);
            Assert.True(operatorActivity.DerivedFromShare);
            Assert.Equal("share-operator", operatorActivity.ShareId);
            Assert.True(operatorActivity.IsCurrentController);
            Assert.True(timeline.TotalEntries >= 1);
            Assert.All(timeline.Entries, entry =>
            {
                var payload = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(entry.Data);
                Assert.Equal("operator-user", payload["principalId"]?.ToString());
            });
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that the control dashboard exposes the active lease and eligible candidates.
    /// </summary>
    [Fact]
    public async Task GetControlDashboardAsync_ReturnsActiveLeaseAndCandidates()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-read-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (service, orchestrator, _) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("control-dashboard", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            await orchestrator.UpsertParticipantAsync(
                "control-dashboard",
                new SessionParticipantUpsertBody("observer-1", "observer-user", SessionRole.Observer, "owner"),
                TestContext.Current.CancellationToken);

            await orchestrator.UpsertParticipantAsync(
                "control-dashboard",
                new SessionParticipantUpsertBody("controller-1", "controller-user", SessionRole.Controller, "owner"),
                TestContext.Current.CancellationToken);

            await orchestrator.GrantControlAsync(
                "control-dashboard",
                new SessionControlGrantBody("controller-1", "owner", 30),
                TestContext.Current.CancellationToken);

            var dashboard = await service.GetControlDashboardAsync("control-dashboard", TestContext.Current.CancellationToken);

            Assert.Equal("controller-1", dashboard.ActiveLease?.ParticipantId);
            Assert.Contains(dashboard.Candidates, x => x.ParticipantId == "controller-1" && x.IsEligible && x.IsCurrentController);
            Assert.Contains(dashboard.Candidates, x => x.ParticipantId == "observer-1" && !x.IsEligible);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that participant activity deduplicates accepted shares for the same principal.
    /// </summary>
    [Fact]
    public async Task GetParticipantActivityAsync_DeduplicatesPrincipalWithMultipleAcceptedShares()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-read-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (service, orchestrator, _) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("dedupe-session", SessionProfile.Balanced, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            await orchestrator.GrantShareAsync(
                "dedupe-session",
                new SessionShareGrantBody("share-1", "operator-user", "owner", SessionRole.Controller),
                TestContext.Current.CancellationToken);
            await orchestrator.AcceptShareAsync(
                "dedupe-session",
                new SessionShareTransitionBody("share-1", "operator-user"),
                TestContext.Current.CancellationToken);

            await orchestrator.GrantShareAsync(
                "dedupe-session",
                new SessionShareGrantBody("share-2", "operator-user", "owner", SessionRole.Controller),
                TestContext.Current.CancellationToken);
            await orchestrator.AcceptShareAsync(
                "dedupe-session",
                new SessionShareTransitionBody("share-2", "operator-user"),
                TestContext.Current.CancellationToken);

            var activity = await service.GetParticipantActivityAsync("dedupe-session", TestContext.Current.CancellationToken);
            var operatorRows = activity.Where(x => string.Equals(x.PrincipalId, "operator-user", StringComparison.OrdinalIgnoreCase)).ToArray();

            Assert.Single(operatorRows);
            Assert.Equal(SessionShareStatus.Accepted, operatorRows[0].ShareStatus);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Verifies that requested/pending share principals are visible in participant activity before acceptance.
    /// </summary>
    [Fact]
    public async Task GetParticipantActivityAsync_IncludesUnmaterializedInvitationRequests()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-read-model-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var (service, orchestrator, _) = await CreateHarnessAsync(rootDirectory);

            await orchestrator.OpenAsync(
                new SessionOpenBody("request-visibility", SessionProfile.Balanced, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            await orchestrator.RequestInvitationAsync(
                "request-visibility",
                new SessionInvitationRequestBody(
                    "request-share-1",
                    "viewer-user",
                    "viewer-user",
                    SessionRole.Observer,
                    "viewer requested access"),
                TestContext.Current.CancellationToken);

            var activity = await service.GetParticipantActivityAsync("request-visibility", TestContext.Current.CancellationToken);
            var requested = Assert.Single(activity, x => string.Equals(x.PrincipalId, "viewer-user", StringComparison.OrdinalIgnoreCase));

            Assert.True(requested.DerivedFromShare);
            Assert.Equal("request-share-1", requested.ShareId);
            Assert.Equal(SessionShareStatus.Requested, requested.ShareStatus);
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    private static async Task<(CollaborationReadModelService Service, InMemorySessionOrchestrator Orchestrator, FileEventStore EventStore)> CreateHarnessAsync(string rootDirectory)
    {
        var options = new FilePersistenceOptions(rootDirectory);
        var eventStore = new FileEventStore(options);
        var commandJournalStore = new FileCommandJournalStore(options);
        var sessionStore = new FileSessionStore(options);
        var registry = new InMemoryConnectorRegistry(
            new FileConnectorCatalogStore(options),
            new FileEndpointSnapshotStore(options),
            eventStore);
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry,
            commandJournalStore,
            sessionStore);

        var service = new CollaborationReadModelService(sessionStore, eventStore, commandJournalStore);
        return (service, orchestrator, eventStore);
    }
}
