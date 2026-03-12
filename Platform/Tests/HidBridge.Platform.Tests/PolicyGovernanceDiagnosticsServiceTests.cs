using HidBridge.Abstractions;
using HidBridge.ControlPlane.Api;
using Xunit;

namespace HidBridge.Platform.Tests;

public sealed class PolicyGovernanceDiagnosticsServiceTests
{
    [Fact]
    public async Task GetSummaryAsync_FiltersVisibleScopesForScopedCaller()
    {
        var policyStore = new InMemoryPolicyStore(
        [
            new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, DateTimeOffset.UtcNow),
            new PolicyScopeSnapshot("scope-b", "tenant-b", "org-b", true, true, true, DateTimeOffset.UtcNow),
        ],
        [
            new PolicyAssignmentSnapshot("scope-a:user-a", "scope-a", "user-a", ["operator.viewer"], DateTimeOffset.UtcNow, "test"),
            new PolicyAssignmentSnapshot("scope-b:user-b", "scope-b", "user-b", ["operator.viewer"], DateTimeOffset.UtcNow, "test"),
        ]);
        var nowUtc = DateTimeOffset.UtcNow;
        var revisionStore = new InMemoryPolicyRevisionStore(
        [
            new PolicyRevisionSnapshot("rev-1", "scope", "scope-a", "scope-a", null, 1, "{}", nowUtc.AddDays(-1), "test"),
            new PolicyRevisionSnapshot("rev-2", "scope", "scope-b", "scope-b", null, 1, "{}", nowUtc.AddDays(-1), "test"),
            new PolicyRevisionSnapshot("rev-3", "assignment", "scope-a:user-a", "scope-a", "user-a", 1, "{}", nowUtc.AddDays(-1), "test"),
        ]);
        var service = new PolicyGovernanceDiagnosticsService(
            policyStore,
            revisionStore,
            new PolicyRevisionLifecycleOptions
            {
                Retention = TimeSpan.FromDays(30),
                MaxRevisionsPerEntity = 2,
            });

        var summary = await service.GetSummaryAsync(
            new ApiCallerContext("user-1", "operator@example.com", "tenant-a", "org-a", ["operator.viewer"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.TotalRevisions);
        Assert.Equal(1, summary.VisibleScopeCount);
        Assert.Equal(1, summary.VisibleAssignmentCount);
        Assert.Single(summary.Scopes);
        Assert.Equal("scope-a", summary.Scopes[0].ScopeId);
    }

    [Fact]
    public async Task GetSummaryAsync_ComputesPruneCandidates()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var service = new PolicyGovernanceDiagnosticsService(
            new InMemoryPolicyStore(
                [new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, nowUtc)],
                []),
            new InMemoryPolicyRevisionStore(
            [
                new PolicyRevisionSnapshot("rev-1", "scope", "scope-a", "scope-a", null, 1, "{}", nowUtc.AddDays(-120), "test"),
                new PolicyRevisionSnapshot("rev-2", "scope", "scope-a", "scope-a", null, 2, "{}", nowUtc.AddDays(-90), "test"),
                new PolicyRevisionSnapshot("rev-3", "scope", "scope-a", "scope-a", null, 3, "{}", nowUtc.AddDays(-1), "test"),
            ]),
            new PolicyRevisionLifecycleOptions
            {
                Retention = TimeSpan.FromDays(30),
                MaxRevisionsPerEntity = 1,
            });

        var summary = await service.GetSummaryAsync(
            new ApiCallerContext("user-1", "operator@example.com", "tenant-a", "org-a", ["operator.admin"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.PruneCandidateCount);
        Assert.Equal(3, summary.TotalRevisions);
    }

    private sealed class InMemoryPolicyStore : IPolicyStore
    {
        private readonly IReadOnlyList<PolicyScopeSnapshot> _scopes;
        private readonly IReadOnlyList<PolicyAssignmentSnapshot> _assignments;

        public InMemoryPolicyStore(IReadOnlyList<PolicyScopeSnapshot> scopes, IReadOnlyList<PolicyAssignmentSnapshot> assignments)
        {
            _scopes = scopes;
            _assignments = assignments;
        }

        public Task UpsertScopeAsync(PolicyScopeSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveScopeAsync(string scopeId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertAssignmentAsync(PolicyAssignmentSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAssignmentAsync(string assignmentId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<PolicyScopeSnapshot>> ListScopesAsync(CancellationToken cancellationToken) => Task.FromResult(_scopes);
        public Task<IReadOnlyList<PolicyAssignmentSnapshot>> ListAssignmentsAsync(CancellationToken cancellationToken) => Task.FromResult(_assignments);
    }

    private sealed class InMemoryPolicyRevisionStore : IPolicyRevisionStore
    {
        private readonly IReadOnlyList<PolicyRevisionSnapshot> _revisions;

        public InMemoryPolicyRevisionStore(IReadOnlyList<PolicyRevisionSnapshot> revisions)
        {
            _revisions = revisions;
        }

        public Task AppendAsync(PolicyRevisionSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<PolicyRevisionSnapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(_revisions);
        public Task<int> PruneAsync(DateTimeOffset retainSinceUtc, int maxRevisionsPerEntity, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
