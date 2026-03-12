using HidBridge.Abstractions;
using HidBridge.SessionOrchestrator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies persisted policy scope and assignment resolution.
/// </summary>
public sealed class OperatorPolicyServiceTests
{
    /// <summary>
    /// Verifies that persisted assignments augment caller roles and fill missing scope.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_AugmentsRolesAndScopeFromPolicyStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPolicyStore>(new StubPolicyStore(
            [new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, DateTimeOffset.UtcNow, true)],
            [new PolicyAssignmentSnapshot("scope-a:user-a", "scope-a", "user-a", ["operator.viewer"], DateTimeOffset.UtcNow, "test")]));
        services.AddSessionOrchestrator();

        using var provider = services.BuildServiceProvider();
        var policyService = provider.GetRequiredService<IOperatorPolicyService>();

        var resolution = await policyService.ResolveAsync("user-a", null, null, [], TestContext.Current.CancellationToken);

        Assert.Equal("tenant-a", resolution.TenantId);
        Assert.Equal("org-a", resolution.OrganizationId);
        Assert.Equal(["operator.viewer"], resolution.OperatorRoles);
        Assert.Single(resolution.MatchedAssignments);
        Assert.Single(resolution.MatchedScopes);
    }

    /// <summary>
    /// Verifies that explicit caller scope is not replaced by unrelated assignments.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_PreservesExplicitScopeWhenAssignmentsDiffer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPolicyStore>(new StubPolicyStore(
            [new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, DateTimeOffset.UtcNow, true)],
            [new PolicyAssignmentSnapshot("scope-a:user-a", "scope-a", "user-a", ["operator.viewer"], DateTimeOffset.UtcNow, "test")]));
        services.AddSessionOrchestrator();

        using var provider = services.BuildServiceProvider();
        var policyService = provider.GetRequiredService<IOperatorPolicyService>();

        var resolution = await policyService.ResolveAsync("user-a", "tenant-b", "org-b", ["operator.moderator"], TestContext.Current.CancellationToken);

        Assert.Equal("tenant-b", resolution.TenantId);
        Assert.Equal("org-b", resolution.OrganizationId);
        Assert.Equal(["operator.moderator", "operator.viewer"], resolution.OperatorRoles);
    }

    /// <summary>
    /// Verifies that inactive assignments no longer contribute caller roles or scope.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_IgnoresInactiveAssignments()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPolicyStore>(new StubPolicyStore(
            [new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, DateTimeOffset.UtcNow, true)],
            [new PolicyAssignmentSnapshot("scope-a:user-a", "scope-a", "user-a", ["operator.viewer"], DateTimeOffset.UtcNow, "test", false)]));
        services.AddSessionOrchestrator();

        using var provider = services.BuildServiceProvider();
        var policyService = provider.GetRequiredService<IOperatorPolicyService>();

        var resolution = await policyService.ResolveAsync("user-a", null, null, [], TestContext.Current.CancellationToken);

        Assert.Null(resolution.TenantId);
        Assert.Null(resolution.OrganizationId);
        Assert.Empty(resolution.OperatorRoles);
        Assert.Empty(resolution.MatchedAssignments);
    }

    /// <summary>
    /// Verifies that inactive scopes suppress otherwise active assignments.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_IgnoresAssignmentsBoundToInactiveScopes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPolicyStore>(new StubPolicyStore(
            [new PolicyScopeSnapshot("scope-a", "tenant-a", "org-a", true, true, true, DateTimeOffset.UtcNow, false)],
            [new PolicyAssignmentSnapshot("scope-a:user-a", "scope-a", "user-a", ["operator.viewer"], DateTimeOffset.UtcNow, "test", true)]));
        services.AddSessionOrchestrator();

        using var provider = services.BuildServiceProvider();
        var policyService = provider.GetRequiredService<IOperatorPolicyService>();

        var resolution = await policyService.ResolveAsync("user-a", null, null, [], TestContext.Current.CancellationToken);

        Assert.Null(resolution.TenantId);
        Assert.Null(resolution.OrganizationId);
        Assert.Empty(resolution.OperatorRoles);
        Assert.Empty(resolution.MatchedAssignments);
        Assert.Empty(resolution.MatchedScopes);
    }

    private sealed class StubPolicyStore : IPolicyStore
    {
        private readonly IReadOnlyList<PolicyScopeSnapshot> _scopes;
        private readonly IReadOnlyList<PolicyAssignmentSnapshot> _assignments;

        public StubPolicyStore(IReadOnlyList<PolicyScopeSnapshot> scopes, IReadOnlyList<PolicyAssignmentSnapshot> assignments)
        {
            _scopes = scopes;
            _assignments = assignments;
        }

        public Task UpsertScopeAsync(PolicyScopeSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveScopeAsync(string scopeId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertAssignmentAsync(PolicyAssignmentSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAssignmentAsync(string assignmentId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PolicyScopeSnapshot>> ListScopesAsync(CancellationToken cancellationToken)
            => Task.FromResult(_scopes);

        public Task<IReadOnlyList<PolicyAssignmentSnapshot>> ListAssignmentsAsync(CancellationToken cancellationToken)
            => Task.FromResult(_assignments);
    }
}
