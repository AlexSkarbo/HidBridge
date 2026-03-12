using HidBridge.Abstractions;
using HidBridge.Contracts;
using HidBridge.SessionOrchestrator;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies retention cleanup and reconciliation behavior in the background maintenance service.
/// </summary>
public sealed class SessionMaintenanceServiceTests
{
    /// <summary>
    /// Verifies that retention cleanup trims both audit and telemetry events older than the configured cutoffs.
    /// </summary>
    [Fact]
    public async Task TrimRetentionAsync_RemovesExpiredAuditAndTelemetryEvents()
    {
        var eventStore = new InMemoryEventStore();
        var eventWriter = new InMemoryEventWriter(eventStore);
        var nowUtc = DateTimeOffset.UtcNow;
        var options = new SessionMaintenanceOptions
        {
            AuditRetention = TimeSpan.FromDays(7),
            TelemetryRetention = TimeSpan.FromDays(3),
        };
        var service = new SessionMaintenanceService(new InMemorySessionStore(), eventStore, eventWriter, options);

        await eventStore.AppendAuditAsync(
            new AuditEventBody("audit", "old", null, null, nowUtc.AddDays(-10)),
            TestContext.Current.CancellationToken);
        await eventStore.AppendAuditAsync(
            new AuditEventBody("audit", "fresh", null, null, nowUtc),
            TestContext.Current.CancellationToken);
        await eventStore.AppendTelemetryAsync(
            new TelemetryEventBody("telemetry", new Dictionary<string, object?>(), null, nowUtc.AddDays(-5)),
            TestContext.Current.CancellationToken);
        await eventStore.AppendTelemetryAsync(
            new TelemetryEventBody("telemetry", new Dictionary<string, object?>(), null, nowUtc),
            TestContext.Current.CancellationToken);

        var deleted = await service.TrimRetentionAsync(TestContext.Current.CancellationToken);
        var audit = await eventStore.ListAuditAsync(TestContext.Current.CancellationToken);
        var telemetry = await eventStore.ListTelemetryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted.AuditDeleted);
        Assert.Equal(1, deleted.TelemetryDeleted);
        Assert.Single(audit);
        Assert.Single(telemetry);
        Assert.Equal("fresh", audit[0].Message);
    }

    /// <summary>
    /// Verifies that reconciliation clears expired control leases and emits an audit event.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_ClearsExpiredControlLease()
    {
        var sessionStore = new InMemorySessionStore();
        var eventStore = new InMemoryEventStore();
        var eventWriter = new InMemoryEventWriter(eventStore);
        var nowUtc = DateTimeOffset.UtcNow;
        var session = new SessionSnapshot(
            "session-control-expired",
            "agent-1",
            "endpoint-1",
            SessionProfile.UltraLowLatency,
            "owner",
            SessionRole.Owner,
            SessionState.Active,
            nowUtc.AddMinutes(-1),
            new[]
            {
                new SessionParticipantSnapshot("owner:owner", "owner", SessionRole.Owner, nowUtc.AddMinutes(-2), nowUtc.AddMinutes(-1)),
            },
            Array.Empty<SessionShareSnapshot>(),
            new SessionControlLeaseBody("owner:owner", "owner", "owner", nowUtc.AddMinutes(-1), nowUtc.AddSeconds(-1)),
            nowUtc,
            nowUtc.AddMinutes(1));

        await sessionStore.UpsertAsync(session, TestContext.Current.CancellationToken);
        var service = new SessionMaintenanceService(sessionStore, eventStore, eventWriter, new SessionMaintenanceOptions());

        var updated = await service.ReconcileAsync(TestContext.Current.CancellationToken);
        var snapshot = Assert.Single(await sessionStore.ListAsync(TestContext.Current.CancellationToken));
        var audit = await eventStore.ListAuditAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, updated);
        Assert.Null(snapshot.ControlLease);
        Assert.Contains(audit, x => x.Message.Contains("Control lease expired", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class InMemorySessionStore : ISessionStore
    {
        private readonly List<SessionSnapshot> _items = new();

        public Task UpsertAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            _items.RemoveAll(x => x.SessionId == snapshot.SessionId);
            _items.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
        {
            _items.RemoveAll(x => x.SessionId == sessionId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SessionSnapshot>>(_items.ToArray());
    }

    private sealed class InMemoryEventWriter : IEventWriter
    {
        private readonly IEventStore _eventStore;

        public InMemoryEventWriter(IEventStore eventStore)
        {
            _eventStore = eventStore;
        }

        public Task WriteAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken)
            => _eventStore.AppendAuditAsync(auditEvent, cancellationToken);

        public Task WriteTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken)
            => _eventStore.AppendTelemetryAsync(telemetryEvent, cancellationToken);
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly List<AuditEventBody> _audit = new();
        private readonly List<TelemetryEventBody> _telemetry = new();

        public Task AppendAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken)
        {
            _audit.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task AppendTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken)
        {
            _telemetry.Add(telemetryEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEventBody>> ListAuditAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AuditEventBody>>(_audit.ToArray());

        public Task<IReadOnlyList<TelemetryEventBody>> ListTelemetryAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TelemetryEventBody>>(_telemetry.ToArray());

        public Task<int> TrimAuditAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
        {
            var deleted = _audit.RemoveAll(x => (x.CreatedAtUtc ?? DateTimeOffset.UtcNow) < olderThanUtc);
            return Task.FromResult(deleted);
        }

        public Task<int> TrimTelemetryAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
        {
            var deleted = _telemetry.RemoveAll(x => (x.CreatedAtUtc ?? DateTimeOffset.UtcNow) < olderThanUtc);
            return Task.FromResult(deleted);
        }
    }
}
