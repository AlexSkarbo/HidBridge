using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using HidBridge.ControlPlane.Api;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies transport SLO aggregation and alert classification.
/// </summary>
public sealed class TransportSloDiagnosticsServiceTests
{
    /// <summary>
    /// Classifies summary as critical when timeout-rate and reconnect-frequency exceed thresholds.
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_ReturnsCritical_WhenTimeoutAndReconnectExceedThresholds()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var sessionStore = new InMemorySessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-slo-1", "endpoint-1", "local-tenant", "local-org", nowUtc), TestContext.Current.CancellationToken);

        var journalStore = new InMemoryCommandJournalStore();
        await journalStore.AppendAsync(
            CreateRelayCommand("cmd-1", "session-slo-1", CommandStatus.Applied, nowUtc.AddMinutes(-8), 120d),
            TestContext.Current.CancellationToken);
        await journalStore.AppendAsync(
            CreateRelayCommand("cmd-2", "session-slo-1", CommandStatus.Timeout, nowUtc.AddMinutes(-7), 550d),
            TestContext.Current.CancellationToken);

        var relay = new WebRtcCommandRelayService();
        _ = await relay.MarkPeerOnlineAsync(
            "session-slo-1",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string>
            {
                ["state"] = "Connecting",
                ["stateChangedAtUtc"] = nowUtc.AddMinutes(-10).ToString("O"),
            },
            TestContext.Current.CancellationToken);
        _ = await relay.MarkPeerOnlineAsync(
            "session-slo-1",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string>
            {
                ["state"] = "Reconnecting",
                ["stateChangedAtUtc"] = nowUtc.AddMinutes(-8).ToString("O"),
            },
            TestContext.Current.CancellationToken);
        _ = await relay.MarkPeerOnlineAsync(
            "session-slo-1",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string>
            {
                ["state"] = "Connected",
                ["stateChangedAtUtc"] = nowUtc.AddMinutes(-6).ToString("O"),
                ["mediaReady"] = "true",
            },
            TestContext.Current.CancellationToken);

        var service = new TransportSloDiagnosticsService(
            sessionStore,
            journalStore,
            relay,
            new TransportSloDiagnosticsOptions
            {
                DefaultWindowMinutes = 60,
                AckTimeoutRateWarn = 0.10d,
                AckTimeoutRateCritical = 0.40d,
                RelayReadyLatencyWarnMs = 30_000d,
                RelayReadyLatencyCriticalMs = 120_000d,
                ReconnectFrequencyWarnPerHour = 1d,
                ReconnectFrequencyCriticalPerHour = 3d,
            });

        var summary = await service.GetSummaryAsync(
            new ApiCallerContext(null, null, null, null, []),
            sessionId: null,
            windowMinutes: 60,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.RelayCommandCount);
        Assert.Equal(1, summary.RelayTimeoutCount);
        Assert.Equal(0.5d, summary.AckTimeoutRate, precision: 3);
        Assert.Equal("critical", summary.Status);
        Assert.Contains(summary.Alerts, alert => alert.Metric == "ack_timeout_rate");
        Assert.Contains(summary.Alerts, alert => alert.Metric == "reconnect_frequency_per_hour");
        Assert.True(summary.Breaches.AckTimeoutRateCritical);
        Assert.True(summary.Breaches.ReconnectFrequencyCriticalPerHour);
        Assert.True(summary.AlertCounters.CriticalCount >= 1);
    }

    /// <summary>
    /// Applies tenant/org scope and excludes sessions outside caller scope.
    /// </summary>
    [Fact]
    public async Task GetSummaryAsync_FiltersToScopedSessions_WhenCallerHasScope()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var sessionStore = new InMemorySessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-local", "endpoint-local", "tenant-a", "org-a", nowUtc), TestContext.Current.CancellationToken);
        await sessionStore.UpsertAsync(CreateSession("session-foreign", "endpoint-foreign", "tenant-b", "org-b", nowUtc), TestContext.Current.CancellationToken);

        var journalStore = new InMemoryCommandJournalStore();
        await journalStore.AppendAsync(
            CreateRelayCommand("cmd-local", "session-local", CommandStatus.Applied, nowUtc.AddMinutes(-5), 90d),
            TestContext.Current.CancellationToken);
        await journalStore.AppendAsync(
            CreateRelayCommand("cmd-foreign", "session-foreign", CommandStatus.Applied, nowUtc.AddMinutes(-5), 95d),
            TestContext.Current.CancellationToken);

        var service = new TransportSloDiagnosticsService(
            sessionStore,
            journalStore,
            new WebRtcCommandRelayService(),
            new TransportSloDiagnosticsOptions());

        var caller = new ApiCallerContext(
            SubjectId: "user-1",
            PrincipalId: "user-1",
            TenantId: "tenant-a",
            OrganizationId: "org-a",
            Roles: ["operator.viewer"]);

        var summary = await service.GetSummaryAsync(
            caller,
            sessionId: null,
            windowMinutes: 60,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.SessionCount);
        Assert.Equal("session-local", Assert.Single(summary.Sessions).SessionId);
        Assert.Equal(1, summary.RelayCommandCount);
        Assert.Equal("ok", summary.Status);
        Assert.Equal(0, summary.AlertCounters.WarningCount);
        Assert.Equal(0, summary.AlertCounters.CriticalCount);
        Assert.False(summary.Breaches.AckTimeoutRateWarn);
        Assert.False(summary.Breaches.AckTimeoutRateCritical);
        Assert.False(summary.Breaches.ReconnectFrequencyWarnPerHour);
        Assert.False(summary.Breaches.ReconnectFrequencyCriticalPerHour);
    }

    /// <summary>
    /// Creates a session snapshot for diagnostics tests.
    /// </summary>
    private static SessionSnapshot CreateSession(
        string sessionId,
        string endpointId,
        string tenantId,
        string organizationId,
        DateTimeOffset nowUtc)
        => new(
            SessionId: sessionId,
            AgentId: "agent-1",
            EndpointId: endpointId,
            Profile: SessionProfile.UltraLowLatency,
            RequestedBy: "owner",
            Role: SessionRole.Owner,
            State: SessionState.Active,
            UpdatedAtUtc: nowUtc,
            Participants:
            [
                new SessionParticipantSnapshot("owner:owner", "owner", SessionRole.Owner, nowUtc, nowUtc),
            ],
            Shares: [],
            ControlLease: null,
            LastHeartbeatAtUtc: nowUtc,
            LeaseExpiresAtUtc: nowUtc.AddMinutes(2),
            TenantId: tenantId,
            OrganizationId: organizationId);

    /// <summary>
    /// Creates one relay command journal entry with roundtrip metric.
    /// </summary>
    private static CommandJournalEntryBody CreateRelayCommand(
        string commandId,
        string sessionId,
        CommandStatus status,
        DateTimeOffset completedAtUtc,
        double roundtripMs)
        => new(
            CommandId: commandId,
            SessionId: sessionId,
            AgentId: "agent-1",
            Channel: CommandChannel.Hid,
            Action: "keyboard.text",
            Args: new Dictionary<string, object?>
            {
                ["transportProvider"] = "webrtc-datachannel",
            },
            TimeoutMs: 5000,
            IdempotencyKey: $"idem-{commandId}",
            Status: status,
            CreatedAtUtc: completedAtUtc.AddMilliseconds(-100),
            CompletedAtUtc: completedAtUtc,
            Error: null,
            Metrics: new Dictionary<string, double>
            {
                ["transportRelayMode"] = 1d,
                ["relayAdapterWsRoundtripMs"] = roundtripMs,
            });

    /// <summary>
    /// In-memory session store for diagnostics tests.
    /// </summary>
    private sealed class InMemorySessionStore : ISessionStore
    {
        private readonly List<SessionSnapshot> _items = [];

        public Task UpsertAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            _items.RemoveAll(item => string.Equals(item.SessionId, snapshot.SessionId, StringComparison.OrdinalIgnoreCase));
            _items.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
        {
            _items.RemoveAll(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SessionSnapshot>>(_items.ToArray());
    }

    /// <summary>
    /// In-memory command journal store for diagnostics tests.
    /// </summary>
    private sealed class InMemoryCommandJournalStore : ICommandJournalStore
    {
        private readonly List<CommandJournalEntryBody> _items = [];

        public Task AppendAsync(CommandJournalEntryBody entry, CancellationToken cancellationToken)
        {
            _items.RemoveAll(item => string.Equals(item.CommandId, entry.CommandId, StringComparison.OrdinalIgnoreCase));
            _items.Add(entry);
            return Task.CompletedTask;
        }

        public Task<CommandJournalEntryBody?> FindByCommandIdAsync(string commandId, CancellationToken cancellationToken)
            => Task.FromResult(_items.FirstOrDefault(item => string.Equals(item.CommandId, commandId, StringComparison.OrdinalIgnoreCase)));

        public Task<CommandJournalEntryBody?> FindByIdempotencyKeyAsync(string sessionId, string idempotencyKey, CancellationToken cancellationToken)
            => Task.FromResult(_items.FirstOrDefault(item =>
                string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.IdempotencyKey, idempotencyKey, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<CommandJournalEntryBody>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CommandJournalEntryBody>>(_items.ToArray());

        public Task<IReadOnlyList<CommandJournalEntryBody>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CommandJournalEntryBody>>(_items
                .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .ToArray());
    }
}
