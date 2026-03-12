using HidBridge.Abstractions;
using HidBridge.ControlPlane.Api;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies policy governance management flows for editable scopes and assignments.
/// </summary>
public sealed class PolicyGovernanceManagementServiceTests
{
    [Fact]
    public async Task ListScopesAndAssignmentsAsync_FiltersByCallerScope()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var store = new InMemoryPolicyStore(
        [
            new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, nowUtc, true),
            new PolicyScopeSnapshot("scope-b", "tenant-b", "org-b", true, true, true, nowUtc, true),
        ],
        [
            new PolicyAssignmentSnapshot("scope-a:user-a", "scope-a", "user-a", ["operator.viewer"], nowUtc, "test"),
            new PolicyAssignmentSnapshot("scope-b:user-b", "scope-b", "user-b", ["operator.viewer"], nowUtc, "test"),
        ]);

        var service = new PolicyGovernanceManagementService(
            store,
            new InMemoryPolicyRevisionStore(),
            new InMemoryEventWriter());

        var caller = new ApiCallerContext("subject-a", "principal-a", "tenant-a", "org-a", ["operator.viewer"]);

        var scopes = await service.ListScopesAsync(caller, TestContext.Current.CancellationToken);
        var assignments = await service.ListAssignmentsAsync(caller, TestContext.Current.CancellationToken);

        Assert.Single(scopes);
        Assert.Equal("scope-a", scopes[0].ScopeId);
        Assert.Single(assignments);
        Assert.Equal("scope-a:user-a", assignments[0].AssignmentId);
    }

    [Fact]
    public async Task UpsertScopeAndAssignmentAsync_AppendsRevisionsAndAudit()
    {
        var store = new InMemoryPolicyStore();
        var revisions = new InMemoryPolicyRevisionStore();
        var events = new InMemoryEventWriter();
        var service = new PolicyGovernanceManagementService(store, revisions, events);
        var caller = new ApiCallerContext("subject-admin", "operator.admin", "tenant-a", "org-a", ["operator.admin"]);

        var scope = await service.UpsertScopeAsync(
            caller,
            new PolicyScopeUpsertBody("scope-a", "tenant-a", "org-a", true, true, true, true),
            TestContext.Current.CancellationToken);

        var assignment = await service.UpsertAssignmentAsync(
            caller,
            new PolicyAssignmentUpsertBody(null, "scope-a", "user-a", ["operator.viewer", "operator.viewer"], "test-ui", true),
            TestContext.Current.CancellationToken);

        var storedScopes = await store.ListScopesAsync(TestContext.Current.CancellationToken);
        var storedAssignments = await store.ListAssignmentsAsync(TestContext.Current.CancellationToken);
        var storedRevisions = await revisions.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("scope-a", scope.ScopeId);
        Assert.Equal("scope-a:user-a", assignment.AssignmentId);
        Assert.Single(storedScopes);
        Assert.Single(storedAssignments);
        Assert.Equal(2, storedRevisions.Count);
        Assert.Equal(2, events.AuditEvents.Count);
        Assert.All(events.AuditEvents, audit => Assert.Equal("policy", audit.Category));
        Assert.Equal(["operator.viewer"], storedAssignments[0].Roles);
        Assert.True(storedAssignments[0].IsActive);
        Assert.True(storedScopes[0].IsActive);
    }

    [Fact]
    public async Task DeactivateAndDeleteAssignmentAsync_AppendsRevisionsAndAudit()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var store = new InMemoryPolicyStore(
            scopes:
            [
                new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, nowUtc, true),
            ],
            assignments:
            [
                new PolicyAssignmentSnapshot("scope-a:user-a", "scope-a", "user-a", ["operator.viewer"], nowUtc, "test", true),
            ]);
        var revisions = new InMemoryPolicyRevisionStore();
        var events = new InMemoryEventWriter();
        var service = new PolicyGovernanceManagementService(store, revisions, events);
        var caller = new ApiCallerContext("subject-admin", "operator.admin", "tenant-a", "org-a", ["operator.admin"]);

        var deactivated = await service.SetAssignmentActivationAsync(caller, "scope-a:user-a", false, TestContext.Current.CancellationToken);
        await service.DeleteAssignmentAsync(caller, "scope-a:user-a", TestContext.Current.CancellationToken);

        var storedAssignments = await store.ListAssignmentsAsync(TestContext.Current.CancellationToken);
        var storedRevisions = await revisions.ListAsync(TestContext.Current.CancellationToken);

        Assert.False(deactivated.IsActive);
        Assert.Empty(storedAssignments);
        Assert.Equal(2, storedRevisions.Count);
        Assert.Equal(2, events.AuditEvents.Count);
        Assert.Contains(events.AuditEvents, audit => audit.Message.Contains("deactivated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events.AuditEvents, audit => audit.Message.Contains("deleted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteScopeAsync_RemovesScopeAndAppendsRevisionAudit()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var store = new InMemoryPolicyStore(
            scopes:
            [
                new PolicyScopeSnapshot("scope-delete", "tenant-a", "org-a", true, true, true, nowUtc, true),
            ]);
        var revisions = new InMemoryPolicyRevisionStore();
        var events = new InMemoryEventWriter();
        var service = new PolicyGovernanceManagementService(store, revisions, events);
        var caller = new ApiCallerContext("subject-admin", "operator.admin", "tenant-a", "org-a", ["operator.admin"]);

        await service.DeleteScopeAsync(caller, "scope-delete", TestContext.Current.CancellationToken);

        Assert.Empty(await store.ListScopesAsync(TestContext.Current.CancellationToken));
        var storedRevisions = await revisions.ListAsync(TestContext.Current.CancellationToken);
        Assert.Single(storedRevisions);
        Assert.Single(events.AuditEvents);
        Assert.Contains(events.AuditEvents, audit => audit.Message.Contains("deleted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteScopeAsync_RejectsScopeWithAssignments()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var store = new InMemoryPolicyStore(
            scopes:
            [
                new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, nowUtc, true),
            ],
            assignments:
            [
                new PolicyAssignmentSnapshot("scope-a:user-a", "scope-a", "user-a", ["operator.viewer"], nowUtc, "test", true),
            ]);
        var service = new PolicyGovernanceManagementService(store, new InMemoryPolicyRevisionStore(), new InMemoryEventWriter());
        var caller = new ApiCallerContext("subject-admin", "operator.admin", "tenant-a", "org-a", ["operator.admin"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteScopeAsync(caller, "scope-a", TestContext.Current.CancellationToken));

        Assert.Contains("Remove assignments before deleting the scope", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeactivateAndActivateScopeAsync_AppendsRevisionsAndAudit()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var store = new InMemoryPolicyStore(
            scopes:
            [
                new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, nowUtc, true),
            ]);
        var revisions = new InMemoryPolicyRevisionStore();
        var events = new InMemoryEventWriter();
        var service = new PolicyGovernanceManagementService(store, revisions, events);
        var caller = new ApiCallerContext("subject-admin", "operator.admin", "tenant-a", "org-a", ["operator.admin"]);

        var deactivated = await service.SetScopeActivationAsync(caller, "scope-a", false, TestContext.Current.CancellationToken);
        var activated = await service.SetScopeActivationAsync(caller, "scope-a", true, TestContext.Current.CancellationToken);

        var storedScopes = await store.ListScopesAsync(TestContext.Current.CancellationToken);
        var storedRevisions = await revisions.ListAsync(TestContext.Current.CancellationToken);

        Assert.False(deactivated.IsActive);
        Assert.True(activated.IsActive);
        Assert.True(storedScopes.Single().IsActive);
        Assert.Equal(2, storedRevisions.Count);
        Assert.Contains(events.AuditEvents, audit => audit.Message.Contains("deactivated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(events.AuditEvents, audit => audit.Message.Contains("activated", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class InMemoryPolicyStore : IPolicyStore
    {
        private readonly List<PolicyScopeSnapshot> _scopes;
        private readonly List<PolicyAssignmentSnapshot> _assignments;

        public InMemoryPolicyStore(
            IReadOnlyList<PolicyScopeSnapshot>? scopes = null,
            IReadOnlyList<PolicyAssignmentSnapshot>? assignments = null)
        {
            _scopes = scopes?.ToList() ?? [];
            _assignments = assignments?.ToList() ?? [];
        }

        public Task UpsertScopeAsync(PolicyScopeSnapshot snapshot, CancellationToken cancellationToken)
        {
            _scopes.RemoveAll(x => string.Equals(x.ScopeId, snapshot.ScopeId, StringComparison.OrdinalIgnoreCase));
            _scopes.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task RemoveScopeAsync(string scopeId, CancellationToken cancellationToken)
        {
            _scopes.RemoveAll(x => string.Equals(x.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task UpsertAssignmentAsync(PolicyAssignmentSnapshot snapshot, CancellationToken cancellationToken)
        {
            _assignments.RemoveAll(x => string.Equals(x.AssignmentId, snapshot.AssignmentId, StringComparison.OrdinalIgnoreCase));
            _assignments.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task RemoveAssignmentAsync(string assignmentId, CancellationToken cancellationToken)
        {
            _assignments.RemoveAll(x => string.Equals(x.AssignmentId, assignmentId, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PolicyScopeSnapshot>> ListScopesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PolicyScopeSnapshot>>(_scopes.ToArray());

        public Task<IReadOnlyList<PolicyAssignmentSnapshot>> ListAssignmentsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PolicyAssignmentSnapshot>>(_assignments.ToArray());
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
            => Task.FromResult(0);
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
