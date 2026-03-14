using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.ConnectorHost;
using HidBridge.Contracts;
using HidBridge.Persistence;
using HidBridge.SessionOrchestrator;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies in-memory session lifecycle and session-bound command routing.
/// </summary>
public sealed class InMemorySessionOrchestratorTests
{
    private sealed class FakeConnector : IConnector
    {
        private readonly Func<CommandRequestBody, ConnectorCommandResult> _execute;

        public FakeConnector(string agentId, string endpointId, Func<CommandRequestBody, ConnectorCommandResult>? execute = null)
        {
            Descriptor = new ConnectorDescriptor(
                agentId,
                endpointId,
                ConnectorType.HidBridge,
                new[] { new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0") });
            _execute = execute ?? (command => new ConnectorCommandResult(command.CommandId, CommandStatus.Applied));
        }

        public ConnectorDescriptor Descriptor { get; }

        public List<CommandRequestBody> Commands { get; } = new();

        public Task<AgentRegisterBody> RegisterAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentRegisterBody(Descriptor.ConnectorType, "test", Descriptor.Capabilities));

        public Task<AgentHeartbeatBody> HeartbeatAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentHeartbeatBody(AgentStatus.Online));

        public Task<ConnectorCommandResult> ExecuteAsync(CommandRequestBody command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(_execute(command));
        }
    }

    [Fact]
    /// <summary>
    /// Verifies that command dispatch resolves the target agent from the session binding.
    /// </summary>
    public async Task DispatchAsync_UsesSessionBoundAgentWhenArgsDoNotOverride()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-1", SessionProfile.UltraLowLatency, "tester", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        var ack = await orchestrator.DispatchAsync(
            new CommandRequestBody(
                "cmd-1",
                "session-1",
                CommandChannel.Hid,
                "keyboard.text",
                new Dictionary<string, object?> { ["text"] = "abc" },
                250,
                "idem-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CommandStatus.Applied, ack.Status);
        var command = Assert.Single(connector.Commands);
        Assert.Equal("keyboard.text", command.Action);
        Assert.Equal("session-1", command.SessionId);
    }

    [Fact]
    /// <summary>
    /// Verifies that one command timeout transitions the room to recovering instead of failed.
    /// </summary>
    public async Task DispatchAsync_TimeoutMovesSessionToRecovering()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector(
            "agent-1",
            "endpoint-1",
            command => new ConnectorCommandResult(
                command.CommandId,
                CommandStatus.Timeout,
                new ErrorInfo(ErrorDomain.Uart, "E_UART_TIMEOUT", "No UART ACK", false)));
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-timeout", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        var ack = await orchestrator.DispatchAsync(
            new CommandRequestBody(
                "cmd-timeout",
                "session-timeout",
                CommandChannel.Hid,
                "keyboard.text",
                new Dictionary<string, object?> { ["text"] = "timeout" },
                250,
                "idem-timeout"),
            TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(await orchestrator.SnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CommandStatus.Timeout, ack.Status);
        Assert.Equal(SessionState.Recovering, snapshot.State);
    }

    [Fact]
    /// <summary>
    /// Verifies that a later successful command returns the room from recovering to active.
    /// </summary>
    public async Task DispatchAsync_AppliedAfterTimeoutReturnsSessionToActive()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector(
            "agent-1",
            "endpoint-1",
            command => command.CommandId == "cmd-timeout"
                ? new ConnectorCommandResult(
                    command.CommandId,
                    CommandStatus.Timeout,
                    new ErrorInfo(ErrorDomain.Uart, "E_UART_TIMEOUT", "No UART ACK", false))
                : new ConnectorCommandResult(command.CommandId, CommandStatus.Applied));
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-timeout-recover", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        _ = await orchestrator.DispatchAsync(
            new CommandRequestBody(
                "cmd-timeout",
                "session-timeout-recover",
                CommandChannel.Hid,
                "keyboard.text",
                new Dictionary<string, object?> { ["text"] = "timeout" },
                250,
                "idem-timeout"),
            TestContext.Current.CancellationToken);

        var recoveringSnapshot = Assert.Single(await orchestrator.SnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SessionState.Recovering, recoveringSnapshot.State);

        var appliedAck = await orchestrator.DispatchAsync(
            new CommandRequestBody(
                "cmd-applied",
                "session-timeout-recover",
                CommandChannel.Hid,
                "keyboard.text",
                new Dictionary<string, object?> { ["text"] = "recover" },
                250,
                "idem-applied"),
            TestContext.Current.CancellationToken);

        var activeSnapshot = Assert.Single(await orchestrator.SnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CommandStatus.Applied, appliedAck.Status);
        Assert.Equal(SessionState.Active, activeSnapshot.State);
    }

    [Fact]
    /// <summary>
    /// Verifies that successful dispatch refreshes the persisted session lease markers.
    /// </summary>
    public async Task DispatchAsync_RefreshesPersistedSessionLeaseMarkers()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemoryConnectorRegistry();
            var connector = new FakeConnector("agent-1", "endpoint-1");
            await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

            var fileOptions = new FilePersistenceOptions(rootDirectory);
            var sessionStore = new FileSessionStore(fileOptions);
            var commandJournalStore = new FileCommandJournalStore(fileOptions);
            var openUseCase = new OpenSessionUseCase(registry);
            var dispatchUseCase = new DispatchCommandUseCase(registry, registry);

            var orchestrator = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            await orchestrator.OpenAsync(
                new SessionOpenBody("session-lease-refresh", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            var persisted = Assert.Single(await sessionStore.ListAsync(TestContext.Current.CancellationToken));
            var staleSnapshot = persisted with
            {
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4),
            };
            await sessionStore.UpsertAsync(staleSnapshot, TestContext.Current.CancellationToken);

            var reloadedOrchestrator = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            var ack = await reloadedOrchestrator.DispatchAsync(
                new CommandRequestBody(
                    "cmd-lease-refresh",
                    "session-lease-refresh",
                    CommandChannel.Hid,
                    "keyboard.text",
                    new Dictionary<string, object?> { ["text"] = "lease" },
                    250,
                    "idem-lease-refresh"),
                TestContext.Current.CancellationToken);

            var updated = Assert.Single(await sessionStore.ListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(CommandStatus.Applied, ack.Status);
            Assert.NotNull(updated.LastHeartbeatAtUtc);
            Assert.NotNull(updated.LeaseExpiresAtUtc);
            Assert.True(updated.LastHeartbeatAtUtc > staleSnapshot.LastHeartbeatAtUtc);
            Assert.True(updated.LeaseExpiresAtUtc > staleSnapshot.LeaseExpiresAtUtc);
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
    /// <summary>
    /// Verifies that a successful command transitions a recovering session back to active.
    /// </summary>
    public async Task DispatchAsync_AppliedCommandRecoversRecoveringSession()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemoryConnectorRegistry();
            var connector = new FakeConnector("agent-1", "endpoint-1");
            await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

            var fileOptions = new FilePersistenceOptions(rootDirectory);
            var sessionStore = new FileSessionStore(fileOptions);
            var commandJournalStore = new FileCommandJournalStore(fileOptions);
            var openUseCase = new OpenSessionUseCase(registry);
            var dispatchUseCase = new DispatchCommandUseCase(registry, registry);

            var orchestrator = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            await orchestrator.OpenAsync(
                new SessionOpenBody("session-recover", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            var persisted = Assert.Single(await sessionStore.ListAsync(TestContext.Current.CancellationToken));
            var recoveringSnapshot = persisted with
            {
                State = SessionState.Recovering,
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            };
            await sessionStore.UpsertAsync(recoveringSnapshot, TestContext.Current.CancellationToken);

            var reloadedOrchestrator = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            var ack = await reloadedOrchestrator.DispatchAsync(
                new CommandRequestBody(
                    "cmd-recover",
                    "session-recover",
                    CommandChannel.Hid,
                    "keyboard.text",
                    new Dictionary<string, object?> { ["text"] = "recover" },
                    250,
                    "idem-recover"),
                TestContext.Current.CancellationToken);

            var updated = Assert.Single(await sessionStore.ListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(CommandStatus.Applied, ack.Status);
            Assert.Equal(SessionState.Active, updated.State);
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
    /// <summary>
    /// Verifies that a successful command transitions a failed session back to active.
    /// </summary>
    public async Task DispatchAsync_AppliedCommandRecoversFailedSession()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemoryConnectorRegistry();
            var connector = new FakeConnector("agent-1", "endpoint-1");
            await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

            var fileOptions = new FilePersistenceOptions(rootDirectory);
            var sessionStore = new FileSessionStore(fileOptions);
            var commandJournalStore = new FileCommandJournalStore(fileOptions);
            var openUseCase = new OpenSessionUseCase(registry);
            var dispatchUseCase = new DispatchCommandUseCase(registry, registry);

            var orchestrator = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            await orchestrator.OpenAsync(
                new SessionOpenBody("session-failed-recover", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            var persisted = Assert.Single(await sessionStore.ListAsync(TestContext.Current.CancellationToken));
            var failedSnapshot = persisted with
            {
                State = SessionState.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            };
            await sessionStore.UpsertAsync(failedSnapshot, TestContext.Current.CancellationToken);

            var reloadedOrchestrator = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            var ack = await reloadedOrchestrator.DispatchAsync(
                new CommandRequestBody(
                    "cmd-failed-recover",
                    "session-failed-recover",
                    CommandChannel.Hid,
                    "keyboard.text",
                    new Dictionary<string, object?> { ["text"] = "recover-failed" },
                    250,
                    "idem-failed-recover"),
                TestContext.Current.CancellationToken);

            var updated = Assert.Single(await sessionStore.ListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(CommandStatus.Applied, ack.Status);
            Assert.Equal(SessionState.Active, updated.State);
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
    /// <summary>
    /// Verifies that closing a session removes it from memory and records an audit trail.
    /// </summary>
    public async Task CloseAsync_RemovesSessionAndWritesAuditEvent()
    {
        var registry = new InMemoryConnectorRegistry();
        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-close", SessionProfile.Balanced, "tester", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.CloseAsync(
            new SessionCloseBody("session-close", "done"),
            TestContext.Current.CancellationToken);

        var sessions = await orchestrator.SnapshotAsync(TestContext.Current.CancellationToken);
        var audit = await registry.AuditSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(sessions, x => string.Equals(x.SessionId, "session-close", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit, x => x.Message.Contains("session-close", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    /// <summary>
    /// Verifies that persisted session snapshots can be reloaded for command routing after an orchestrator restart.
    /// </summary>
    public async Task DispatchAsync_ReloadsPersistedSessionBinding()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemoryConnectorRegistry();
            var connector = new FakeConnector("agent-1", "endpoint-1");
            await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

            var fileOptions = new FilePersistenceOptions(rootDirectory);
            var sessionStore = new FileSessionStore(fileOptions);
            var commandJournalStore = new FileCommandJournalStore(fileOptions);
            var openUseCase = new OpenSessionUseCase(registry);
            var dispatchUseCase = new DispatchCommandUseCase(registry, registry);

            var orchestrator1 = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            await orchestrator1.OpenAsync(
                new SessionOpenBody("session-persist", SessionProfile.UltraLowLatency, "tester", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            var orchestrator2 = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            var ack = await orchestrator2.DispatchAsync(
                new CommandRequestBody(
                    "cmd-persist",
                    "session-persist",
                    CommandChannel.Hid,
                    "keyboard.text",
                    new Dictionary<string, object?> { ["text"] = "reload" },
                    250,
                    "idem-persist"),
                TestContext.Current.CancellationToken);

            Assert.Equal(CommandStatus.Applied, ack.Status);
            Assert.Equal(1, connector.Commands.Count(x => x.CommandId == "cmd-persist"));
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
    /// Verifies that dispatch persists a command journal entry with the resolved agent binding.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_PersistsCommandJournalEntry()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemoryConnectorRegistry();
            var connector = new FakeConnector("agent-1", "endpoint-1");
            await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

            var fileOptions = new FilePersistenceOptions(rootDirectory);
            var commandJournalStore = new FileCommandJournalStore(fileOptions);
            var orchestrator = new InMemorySessionOrchestrator(
                new OpenSessionUseCase(registry),
                new DispatchCommandUseCase(registry, registry),
                registry,
                commandJournalStore,
                new FileSessionStore(fileOptions));

            await orchestrator.OpenAsync(
                new SessionOpenBody("session-journal", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            await orchestrator.DispatchAsync(
                new CommandRequestBody(
                    "cmd-journal-1",
                    "session-journal",
                    CommandChannel.Hid,
                    "keyboard.text",
                    new Dictionary<string, object?> { ["text"] = "abc" },
                    250,
                    "idem-journal-1"),
                TestContext.Current.CancellationToken);

            var entries = await commandJournalStore.ListBySessionAsync("session-journal", TestContext.Current.CancellationToken);
            var entry = Assert.Single(entries);

            Assert.Equal("cmd-journal-1", entry.CommandId);
            Assert.Equal("agent-1", entry.AgentId);
            Assert.Equal(CommandStatus.Applied, entry.Status);
            Assert.NotNull(entry.CompletedAtUtc);
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
    /// Verifies that repeated dispatch of the same command identifier is handled as an idempotent replay.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_ReturnsPersistedAckForDuplicateCommandId()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemoryConnectorRegistry();
            var connector = new FakeConnector("agent-1", "endpoint-1");
            await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

            var fileOptions = new FilePersistenceOptions(rootDirectory);
            var commandJournalStore = new FileCommandJournalStore(fileOptions);
            var orchestrator = new InMemorySessionOrchestrator(
                new OpenSessionUseCase(registry),
                new DispatchCommandUseCase(registry, registry),
                registry,
                commandJournalStore,
                new FileSessionStore(fileOptions));

            await orchestrator.OpenAsync(
                new SessionOpenBody("session-idem", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            var request = new CommandRequestBody(
                "cmd-idem-1",
                "session-idem",
                CommandChannel.Hid,
                "keyboard.text",
                new Dictionary<string, object?> { ["text"] = "abc" },
                250,
                "idem-1");

            var firstAck = await orchestrator.DispatchAsync(request, TestContext.Current.CancellationToken);
            var secondAck = await orchestrator.DispatchAsync(request, TestContext.Current.CancellationToken);
            var entries = await commandJournalStore.ListBySessionAsync("session-idem", TestContext.Current.CancellationToken);

            Assert.Equal(CommandStatus.Applied, firstAck.Status);
            Assert.Equal(firstAck, secondAck);
            Assert.Single(entries);
            Assert.Single(connector.Commands);
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
    /// Verifies that a non-moderator cannot approve invitation requests.
    /// </summary>
    [Fact]
    public async Task ApproveInvitationAsync_RejectsNonModerator()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-policy", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.RequestInvitationAsync(
            "session-policy",
            new SessionInvitationRequestBody("share-1", "viewer", "viewer", SessionRole.Observer),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orchestrator.ApproveInvitationAsync(
            "session-policy",
            new SessionInvitationDecisionBody("share-1", "viewer"),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that control requests create an active control lease and permit command dispatch.
    /// </summary>
    [Fact]
    public async Task RequestControlAsync_GrantsLeaseAndAllowsDispatch()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-control", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        var lease = await orchestrator.RequestControlAsync(
            "session-control",
            new SessionControlRequestBody("owner:owner", "owner", 30, "initial control"),
            TestContext.Current.CancellationToken);

        var ack = await orchestrator.DispatchAsync(
            new CommandRequestBody(
                "cmd-control-1",
                "session-control",
                CommandChannel.Hid,
                "keyboard.text",
                new Dictionary<string, object?>
                {
                    ["text"] = "hello",
                    ["principalId"] = "owner",
                    ["participantId"] = "owner:owner",
                },
                250,
                "idem-control-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal("owner:owner", lease.ParticipantId);
        Assert.Equal(CommandStatus.Applied, ack.Status);
    }

    /// <summary>
    /// Verifies that only the active controller may dispatch while a control lease is held.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_RejectsPrincipalWithoutActiveControlLease()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-control-policy", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.UpsertParticipantAsync(
            "session-control-policy",
            new SessionParticipantUpsertBody("controller-1", "controller-user", SessionRole.Controller, "owner"),
            TestContext.Current.CancellationToken);

        await orchestrator.GrantControlAsync(
            "session-control-policy",
            new SessionControlGrantBody("controller-1", "owner", 30, "moderated grant"),
            TestContext.Current.CancellationToken);

        var ack = await orchestrator.DispatchAsync(
            new CommandRequestBody(
                "cmd-control-denied",
                "session-control-policy",
                CommandChannel.Hid,
                "keyboard.text",
                new Dictionary<string, object?>
                {
                    ["text"] = "blocked",
                    ["principalId"] = "owner",
                    ["participantId"] = "owner:owner",
                },
                250,
                "idem-control-denied"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CommandStatus.Rejected, ack.Status);
        Assert.Equal("E_SESSION_CONTROL_REQUIRED", ack.Error?.Code);
    }

    /// <summary>
    /// Verifies that the owner can force-take over control from another controller.
    /// </summary>
    [Fact]
    public async Task ForceTakeoverControlAsync_OverridesExistingController()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-force", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.UpsertParticipantAsync(
            "session-force",
            new SessionParticipantUpsertBody("controller-1", "controller-user", SessionRole.Controller, "owner"),
            TestContext.Current.CancellationToken);

        await orchestrator.GrantControlAsync(
            "session-force",
            new SessionControlGrantBody("controller-1", "owner", 30),
            TestContext.Current.CancellationToken);

        var takeover = await orchestrator.ForceTakeoverControlAsync(
            "session-force",
            new SessionControlGrantBody("owner:owner", "owner", 30, "owner override"),
            TestContext.Current.CancellationToken);

        Assert.Equal("owner:owner", takeover.ParticipantId);
        Assert.Equal("owner", takeover.PrincipalId);
    }

    /// <summary>
    /// Verifies the deterministic request/grant/force/release handoff sequence and conflict payload semantics.
    /// </summary>
    [Fact]
    public async Task ControlHandoffSequence_EnforcesDeterministicConflictsAndRecovery()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-control-sequence", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.UpsertParticipantAsync(
            "session-control-sequence",
            new SessionParticipantUpsertBody("controller-1", "controller-1", SessionRole.Controller, "owner"),
            TestContext.Current.CancellationToken);

        await orchestrator.UpsertParticipantAsync(
            "session-control-sequence",
            new SessionParticipantUpsertBody("controller-2", "controller-2", SessionRole.Controller, "owner"),
            TestContext.Current.CancellationToken);

        var granted = await orchestrator.GrantControlAsync(
            "session-control-sequence",
            new SessionControlGrantBody("controller-1", "owner", 30, "initial grant"),
            TestContext.Current.CancellationToken);
        Assert.Equal("controller-1", granted.ParticipantId);
        Assert.Equal("controller-1", granted.PrincipalId);

        var leaseConflict = await Assert.ThrowsAsync<ControlArbitrationException>(() => orchestrator.RequestControlAsync(
            "session-control-sequence",
            new SessionControlRequestBody("controller-2", "controller-2", 30, "request while lease held"),
            TestContext.Current.CancellationToken));
        Assert.Equal("E_CONTROL_LEASE_HELD_BY_OTHER", leaseConflict.Code);
        Assert.Equal("controller-2", leaseConflict.RequestedParticipantId);
        Assert.Equal("controller-1", leaseConflict.CurrentControllerParticipantId);

        var takeover = await orchestrator.ForceTakeoverControlAsync(
            "session-control-sequence",
            new SessionControlGrantBody("controller-2", "owner", 30, "force takeover"),
            TestContext.Current.CancellationToken);
        Assert.Equal("controller-2", takeover.ParticipantId);
        Assert.Equal("controller-2", takeover.PrincipalId);

        var mismatchConflict = await Assert.ThrowsAsync<ControlArbitrationException>(() => orchestrator.ReleaseControlAsync(
            "session-control-sequence",
            new SessionControlReleaseBody("owner", "controller-1", "wrong participant"),
            TestContext.Current.CancellationToken));
        Assert.Equal("E_CONTROL_PARTICIPANT_MISMATCH", mismatchConflict.Code);
        Assert.Equal("controller-1", mismatchConflict.RequestedParticipantId);
        Assert.Equal("controller-2", mismatchConflict.CurrentControllerParticipantId);

        var released = await orchestrator.ReleaseControlAsync(
            "session-control-sequence",
            new SessionControlReleaseBody("owner", "controller-2", "final release"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(released);
        Assert.Equal("controller-2", released!.ParticipantId);

        var activeLease = await orchestrator.GetControlLeaseAsync("session-control-sequence", TestContext.Current.CancellationToken);
        Assert.Null(activeLease);
    }

    /// <summary>
    /// Verifies that request-control conflicts return deterministic lease-held-by-other metadata.
    /// </summary>
    [Fact]
    public async Task RequestControlAsync_WhenLeaseHeldByOther_ThrowsControlArbitrationException()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-control-conflict", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.UpsertParticipantAsync(
            "session-control-conflict",
            new SessionParticipantUpsertBody("controller-1", "controller-1", SessionRole.Controller, "owner"),
            TestContext.Current.CancellationToken);

        await orchestrator.UpsertParticipantAsync(
            "session-control-conflict",
            new SessionParticipantUpsertBody("controller-2", "controller-2", SessionRole.Controller, "owner"),
            TestContext.Current.CancellationToken);

        await orchestrator.GrantControlAsync(
            "session-control-conflict",
            new SessionControlGrantBody("controller-1", "owner", 30),
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ControlArbitrationException>(() => orchestrator.RequestControlAsync(
            "session-control-conflict",
            new SessionControlRequestBody("controller-2", "controller-2", 30, "take control"),
            TestContext.Current.CancellationToken));

        Assert.Equal("E_CONTROL_LEASE_HELD_BY_OTHER", ex.Code);
        Assert.Equal("session-control-conflict", ex.SessionId);
        Assert.Equal("controller-1", ex.CurrentControllerParticipantId);
        Assert.Equal("controller-2", ex.RequestedParticipantId);
        Assert.Equal("controller-2", ex.ActedBy);
    }

    /// <summary>
    /// Verifies that release-control conflicts return deterministic participant-mismatch metadata.
    /// </summary>
    [Fact]
    public async Task ReleaseControlAsync_WhenParticipantMismatch_ThrowsControlArbitrationException()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-control-release-conflict", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.GrantControlAsync(
            "session-control-release-conflict",
            new SessionControlGrantBody("owner:owner", "owner", 30),
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ControlArbitrationException>(() => orchestrator.ReleaseControlAsync(
            "session-control-release-conflict",
            new SessionControlReleaseBody("owner", "controller-1"),
            TestContext.Current.CancellationToken));

        Assert.Equal("E_CONTROL_PARTICIPANT_MISMATCH", ex.Code);
        Assert.Equal("owner:owner", ex.CurrentControllerParticipantId);
        Assert.Equal("controller-1", ex.RequestedParticipantId);
        Assert.Equal("owner", ex.ActedBy);
    }

    /// <summary>
    /// Verifies that control leases are clamped to configured min/max boundaries.
    /// </summary>
    [Fact]
    public async Task RequestControlAsync_ClampsLeaseDurationWithinConfiguredBounds()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemoryConnectorRegistry();
            var connector = new FakeConnector("agent-1", "endpoint-1");
            await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

            var fileOptions = new FilePersistenceOptions(rootDirectory);
            var orchestrator = new InMemorySessionOrchestrator(
                new OpenSessionUseCase(registry),
                new DispatchCommandUseCase(registry, registry),
                registry,
                new FileCommandJournalStore(fileOptions),
                new FileSessionStore(fileOptions),
                new SessionMaintenanceOptions
                {
                    ControlLeaseDuration = TimeSpan.FromSeconds(30),
                    MinControlLeaseDuration = TimeSpan.FromSeconds(5),
                    MaxControlLeaseDuration = TimeSpan.FromSeconds(60),
                });

            await orchestrator.OpenAsync(
                new SessionOpenBody("session-control-clamp", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            var shortLease = await orchestrator.RequestControlAsync(
                "session-control-clamp",
                new SessionControlRequestBody("owner:owner", "owner", 1),
                TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromSeconds(5), shortLease.ExpiresAtUtc - shortLease.GrantedAtUtc);

            _ = await orchestrator.ReleaseControlAsync(
                "session-control-clamp",
                new SessionControlReleaseBody("owner"),
                TestContext.Current.CancellationToken);

            var longLease = await orchestrator.RequestControlAsync(
                "session-control-clamp",
                new SessionControlRequestBody("owner:owner", "owner", 3600),
                TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromSeconds(60), longLease.ExpiresAtUtc - longLease.GrantedAtUtc);
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
    /// Verifies that reading control lease clears stale expired state from persisted session snapshots.
    /// </summary>
    [Fact]
    public async Task GetControlLeaseAsync_ClearsExpiredLeaseFromSnapshot()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemoryConnectorRegistry();
            var connector = new FakeConnector("agent-1", "endpoint-1");
            await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

            var fileOptions = new FilePersistenceOptions(rootDirectory);
            var sessionStore = new FileSessionStore(fileOptions);
            var commandJournalStore = new FileCommandJournalStore(fileOptions);
            var openUseCase = new OpenSessionUseCase(registry);
            var dispatchUseCase = new DispatchCommandUseCase(registry, registry);

            var orchestrator = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            await orchestrator.OpenAsync(
                new SessionOpenBody("session-expired-control", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
                TestContext.Current.CancellationToken);

            var lease = await orchestrator.GrantControlAsync(
                "session-expired-control",
                new SessionControlGrantBody("owner:owner", "owner", 30),
                TestContext.Current.CancellationToken);

            var staleSnapshot = (Assert.Single(await sessionStore.ListAsync(TestContext.Current.CancellationToken))) with
            {
                ControlLease = lease with
                {
                    GrantedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                },
            };
            await sessionStore.UpsertAsync(staleSnapshot, TestContext.Current.CancellationToken);

            var reloadedOrchestrator = new InMemorySessionOrchestrator(openUseCase, dispatchUseCase, registry, commandJournalStore, sessionStore);
            var resolvedLease = await reloadedOrchestrator.GetControlLeaseAsync("session-expired-control", TestContext.Current.CancellationToken);

            var updated = Assert.Single(await sessionStore.ListAsync(TestContext.Current.CancellationToken));
            Assert.Null(resolvedLease);
            Assert.Null(updated.ControlLease);
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
    /// Verifies that opening a session materializes owner participant and lease metadata.
    /// </summary>
    [Fact]
    public async Task OpenAsync_PopulatesOwnerParticipantAndLease()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemoryConnectorRegistry();
            var connector = new FakeConnector("agent-1", "endpoint-1");
            await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

            var orchestrator = new InMemorySessionOrchestrator(
                new OpenSessionUseCase(registry),
                new DispatchCommandUseCase(registry, registry),
                registry,
                new FileCommandJournalStore(new FilePersistenceOptions(rootDirectory)),
                new FileSessionStore(new FilePersistenceOptions(rootDirectory)),
                new SessionMaintenanceOptions { LeaseDuration = TimeSpan.FromSeconds(45) });

            await orchestrator.OpenAsync(
                new SessionOpenBody("session-owner", SessionProfile.UltraLowLatency, "tester", "agent-1", "endpoint-1", SessionRole.Owner, "tenant-a", "org-a"),
                TestContext.Current.CancellationToken);

            var snapshot = Assert.Single(await orchestrator.SnapshotAsync(TestContext.Current.CancellationToken));
            var participant = Assert.Single(snapshot.Participants ?? Array.Empty<SessionParticipantSnapshot>());

            Assert.Equal("tester", participant.PrincipalId);
            Assert.Equal(SessionRole.Owner, participant.Role);
            Assert.Equal("tenant-a", snapshot.TenantId);
            Assert.Equal("org-a", snapshot.OrganizationId);
            Assert.True(snapshot.LastHeartbeatAtUtc.HasValue);
            Assert.True(snapshot.LeaseExpiresAtUtc.HasValue);
            Assert.True(snapshot.LeaseExpiresAtUtc > snapshot.LastHeartbeatAtUtc);
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
    /// Verifies that accepting a share materializes the grantee as a participant.
    /// </summary>
    [Fact]
    public async Task AcceptShareAsync_MaterializesParticipant()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-share-accept", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.GrantShareAsync(
            "session-share-accept",
            new SessionShareGrantBody("share-1", "viewer-1", "owner", SessionRole.Observer),
            TestContext.Current.CancellationToken);

        var share = await orchestrator.AcceptShareAsync(
            "session-share-accept",
            new SessionShareTransitionBody("share-1", "viewer-1"),
            TestContext.Current.CancellationToken);

        var participants = await orchestrator.ListParticipantsAsync("session-share-accept", TestContext.Current.CancellationToken);

        Assert.Equal(SessionShareStatus.Accepted, share.Status);
        Assert.Contains(participants, x => x.ParticipantId == "share:share-1" && x.PrincipalId == "viewer-1");
    }

    /// <summary>
    /// Verifies that revoking an accepted share removes the derived participant.
    /// </summary>
    [Fact]
    public async Task RevokeShareAsync_RemovesDerivedParticipant()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-share-revoke", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.GrantShareAsync(
            "session-share-revoke",
            new SessionShareGrantBody("share-2", "controller-1", "owner", SessionRole.Controller),
            TestContext.Current.CancellationToken);

        await orchestrator.AcceptShareAsync(
            "session-share-revoke",
            new SessionShareTransitionBody("share-2", "controller-1"),
            TestContext.Current.CancellationToken);

        var revoked = await orchestrator.RevokeShareAsync(
            "session-share-revoke",
            new SessionShareTransitionBody("share-2", "owner", "manual revoke"),
            TestContext.Current.CancellationToken);

        var participants = await orchestrator.ListParticipantsAsync("session-share-revoke", TestContext.Current.CancellationToken);

        Assert.Equal(SessionShareStatus.Revoked, revoked.Status);
        Assert.DoesNotContain(participants, x => x.ParticipantId == "share:share-2");
    }

    /// <summary>
    /// Verifies that explicit participant upsert and remove flows persist in session snapshots.
    /// </summary>
    [Fact]
    public async Task ParticipantCrud_FlowsThroughSessionSnapshot()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-participant-crud", SessionProfile.Balanced, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.UpsertParticipantAsync(
            "session-participant-crud",
            new SessionParticipantUpsertBody("participant-1", "observer-1", SessionRole.Observer, "owner"),
            TestContext.Current.CancellationToken);

        await orchestrator.RemoveParticipantAsync(
            "session-participant-crud",
            new SessionParticipantRemoveBody("participant-1", "owner", "done"),
            TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(await orchestrator.SnapshotAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(snapshot.Participants ?? Array.Empty<SessionParticipantSnapshot>(), x => x.ParticipantId == "participant-1");
    }

    /// <summary>
    /// Verifies that an invitation request can be approved into a pending share invite.
    /// </summary>
    [Fact]
    public async Task ApproveInvitationAsync_TransitionsRequestedInvitationToPending()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-invite-approve", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.RequestInvitationAsync(
            "session-invite-approve",
            new SessionInvitationRequestBody("invite-1", "viewer-1", "viewer-1", SessionRole.Observer, "need access"),
            TestContext.Current.CancellationToken);

        var approved = await orchestrator.ApproveInvitationAsync(
            "session-invite-approve",
            new SessionInvitationDecisionBody("invite-1", "owner", SessionRole.Controller, "approved"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionShareStatus.Pending, approved.Status);
        Assert.Equal("owner", approved.GrantedBy);
        Assert.Equal(SessionRole.Controller, approved.Role);
    }

    /// <summary>
    /// Verifies that declining an invitation request finalizes it as rejected.
    /// </summary>
    [Fact]
    public async Task DeclineInvitationAsync_TransitionsRequestedInvitationToRejected()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-invite-decline", SessionProfile.Balanced, "owner", "agent-1", "endpoint-1"),
            TestContext.Current.CancellationToken);

        await orchestrator.RequestInvitationAsync(
            "session-invite-decline",
            new SessionInvitationRequestBody("invite-2", "observer-1", "observer-1", SessionRole.Observer, "please"),
            TestContext.Current.CancellationToken);

        var declined = await orchestrator.DeclineInvitationAsync(
            "session-invite-decline",
            new SessionInvitationDecisionBody("invite-2", "owner", Reason: "denied"),
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionShareStatus.Rejected, declined.Status);
        Assert.Equal("observer-1", declined.PrincipalId);
    }

    /// <summary>
    /// Verifies that tenant and organization scoped sessions reject participant mutations from foreign callers.
    /// </summary>
    [Fact]
    public async Task UpsertParticipantAsync_RejectsTenantScopeMismatch()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-scope-participant", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1", SessionRole.Owner, "tenant-a", "org-a"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orchestrator.UpsertParticipantAsync(
            "session-scope-participant",
            new SessionParticipantUpsertBody("participant-foreign", "observer-foreign", SessionRole.Observer, "owner", "tenant-b", "org-a"),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that an administrator can bypass tenant or organization mismatch inside the orchestrator policy layer.
    /// </summary>
    [Fact]
    public async Task UpsertParticipantAsync_AllowsAdministratorTenantOverride()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-scope-admin", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1", SessionRole.Owner, "tenant-a", "org-a"),
            TestContext.Current.CancellationToken);

        var participant = await orchestrator.UpsertParticipantAsync(
            "session-scope-admin",
            new SessionParticipantUpsertBody(
                "participant-admin",
                "observer-admin",
                SessionRole.Observer,
                "admin-user",
                "tenant-b",
                "org-b",
                ["operator.admin"]),
            TestContext.Current.CancellationToken);

        Assert.Equal("participant-admin", participant.ParticipantId);
    }

    /// <summary>
    /// Verifies that moderator role can approve invitations without owning or controlling the session.
    /// </summary>
    [Fact]
    public async Task ApproveInvitationAsync_AllowsModeratorRole()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-moderator", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1", SessionRole.Owner, "tenant-a", "org-a"),
            TestContext.Current.CancellationToken);

        await orchestrator.RequestInvitationAsync(
            "session-moderator",
            new SessionInvitationRequestBody("invite-moderated", "viewer-1", "viewer-1", SessionRole.Observer, "need access", "tenant-a", "org-a", ["operator.viewer"]),
            TestContext.Current.CancellationToken);

        var approved = await orchestrator.ApproveInvitationAsync(
            "session-moderator",
            new SessionInvitationDecisionBody("invite-moderated", "moderator-user", SessionRole.Observer, "approved", "tenant-a", "org-a", ["operator.moderator"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(SessionShareStatus.Pending, approved.Status);
        Assert.Equal("moderator-user", approved.GrantedBy);
    }

    /// <summary>
    /// Verifies that session open is rejected when the caller carries only non-operator roles.
    /// </summary>
    [Fact]
    public async Task OpenAsync_RejectsCallerWithoutViewerRole()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orchestrator.OpenAsync(
            new SessionOpenBody(
                "session-open-policy",
                SessionProfile.UltraLowLatency,
                "viewerless",
                "agent-1",
                "endpoint-1",
                SessionRole.Owner,
                OperatorRoles: ["offline_access"]),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that command dispatch rejects requests from a foreign tenant or organization scope.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_RejectsTenantScopeMismatch()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-scope-dispatch", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1", SessionRole.Owner, "tenant-a", "org-a"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orchestrator.DispatchAsync(
            new CommandRequestBody(
                "cmd-foreign",
                "session-scope-dispatch",
                CommandChannel.Hid,
                "keyboard.text",
                new Dictionary<string, object?>
                {
                    ["text"] = "blocked",
                    ["principalId"] = "owner",
                    ["participantId"] = "owner:owner",
                },
                250,
                "idem-foreign",
                "tenant-b",
                "org-a"),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that control arbitration rejects foreign tenant or organization scopes.
    /// </summary>
    [Fact]
    public async Task RequestControlAsync_RejectsTenantScopeMismatch()
    {
        var registry = new InMemoryConnectorRegistry();
        var connector = new FakeConnector("agent-1", "endpoint-1");
        await registry.RegisterAsync(connector, TestContext.Current.CancellationToken);

        var orchestrator = new InMemorySessionOrchestrator(
            new OpenSessionUseCase(registry),
            new DispatchCommandUseCase(registry, registry),
            registry);

        await orchestrator.OpenAsync(
            new SessionOpenBody("session-scope-control", SessionProfile.UltraLowLatency, "owner", "agent-1", "endpoint-1", SessionRole.Owner, "tenant-a", "org-a"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => orchestrator.RequestControlAsync(
            "session-scope-control",
            new SessionControlRequestBody("owner:owner", "owner", 30, "request", "tenant-b", "org-a"),
            TestContext.Current.CancellationToken));
    }

}
