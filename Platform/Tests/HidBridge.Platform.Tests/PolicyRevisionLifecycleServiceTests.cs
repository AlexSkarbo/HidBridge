using HidBridge.Abstractions;
using HidBridge.ControlPlane.Api;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies retention maintenance for policy revision history.
/// </summary>
public sealed class PolicyRevisionLifecycleServiceTests
{
    [Fact]
    public async Task TrimAsync_PrunesAndWritesAuditEvent()
    {
        var store = new InMemoryPolicyRevisionStore();
        var writer = new InMemoryEventWriter();
        var nowUtc = DateTimeOffset.UtcNow;
        await store.AppendAsync(new PolicyRevisionSnapshot("rev-1", "scope", "scope-a", "scope-a", null, 1, "{}", nowUtc.AddDays(-120), "test"), TestContext.Current.CancellationToken);
        await store.AppendAsync(new PolicyRevisionSnapshot("rev-2", "scope", "scope-a", "scope-a", null, 2, "{}", nowUtc.AddDays(-5), "test"), TestContext.Current.CancellationToken);

        var service = new PolicyRevisionLifecycleService(
            store,
            writer,
            new PolicyRevisionLifecycleOptions
            {
                Retention = TimeSpan.FromDays(30),
                MaxRevisionsPerEntity = 1,
            });

        var deleted = await service.TrimAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Single(writer.AuditEvents);
        Assert.Equal("policy.retention", writer.AuditEvents[0].Category);
    }

    private sealed class InMemoryPolicyRevisionStore : IPolicyRevisionStore
    {
        private readonly List<PolicyRevisionSnapshot> _items = [];

        public Task AppendAsync(PolicyRevisionSnapshot snapshot, CancellationToken cancellationToken)
        {
            _items.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PolicyRevisionSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PolicyRevisionSnapshot>>(_items.ToArray());

        public Task<int> PruneAsync(DateTimeOffset retainSinceUtc, int maxRevisionsPerEntity, CancellationToken cancellationToken)
        {
            var keep = _items
                .GroupBy(static item => (item.EntityType, item.EntityId))
                .SelectMany(group => group
                    .OrderByDescending(static item => item.CreatedAtUtc)
                    .ThenByDescending(static item => item.Version)
                    .Take(Math.Max(maxRevisionsPerEntity, 1))
                    .Select(static item => item.RevisionId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            keep.UnionWith(_items.Where(item => item.CreatedAtUtc >= retainSinceUtc).Select(static item => item.RevisionId));

            var deleted = _items.RemoveAll(item => !keep.Contains(item.RevisionId));
            return Task.FromResult(deleted);
        }
    }

    private sealed class InMemoryEventWriter : IEventWriter
    {
        public List<AuditEventBody> AuditEvents { get; } = [];

        public Task WriteAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken)
        {
            AuditEvents.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task WriteTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
