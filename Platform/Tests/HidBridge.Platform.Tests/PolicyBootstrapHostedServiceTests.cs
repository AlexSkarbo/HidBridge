using HidBridge.Abstractions;
using HidBridge.ControlPlane.Api;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies startup policy bootstrap behavior.
/// </summary>
public sealed class PolicyBootstrapHostedServiceTests
{
    /// <summary>
    /// Verifies that startup bootstrap writes policy scope, assignment, revision, and audit records.
    /// </summary>
    [Fact]
    public async Task StartAsync_WritesPolicyScopeAssignmentRevisionAndAudit()
    {
        var policyStore = new InMemoryPolicyStore();
        var revisionStore = new InMemoryPolicyRevisionStore();
        var eventWriter = new InMemoryEventWriter();
        var service = new PolicyBootstrapHostedService(
            policyStore,
            revisionStore,
            eventWriter,
            new PolicyBootstrapOptions
            {
                ScopeId = "scope-local",
                TenantId = "tenant-a",
                OrganizationId = "org-a",
                Assignments =
                [
                    new PolicyBootstrapAssignment("user-a", ["operator.viewer"], "test"),
                ],
            });

        await service.StartAsync(TestContext.Current.CancellationToken);

        var scopes = await policyStore.ListScopesAsync(TestContext.Current.CancellationToken);
        var assignments = await policyStore.ListAssignmentsAsync(TestContext.Current.CancellationToken);
        var revisions = await revisionStore.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(scopes);
        Assert.Single(assignments);
        Assert.Equal(2, revisions.Count);
        Assert.Equal(2, eventWriter.AuditEvents.Count);
        Assert.Contains(eventWriter.AuditEvents, x => x.Category == "policy");
    }

    private sealed class InMemoryPolicyStore : IPolicyStore
    {
        private readonly List<PolicyScopeSnapshot> _scopes = [];
        private readonly List<PolicyAssignmentSnapshot> _assignments = [];

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
