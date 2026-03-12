using HidBridge.Abstractions;
using HidBridge.Persistence;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies the file-backed policy persistence baseline.
/// </summary>
public sealed class FilePolicyStoreTests
{
    [Fact]
    public async Task UpsertAndListAsync_RoundTripsPolicyScopesAndAssignments()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-policy-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FilePolicyStore(new FilePersistenceOptions(rootDirectory));
            var nowUtc = DateTimeOffset.UtcNow;

            await store.UpsertScopeAsync(
                new PolicyScopeSnapshot("scope-local", "tenant-a", "org-a", true, true, true, nowUtc, false),
                TestContext.Current.CancellationToken);
            await store.UpsertAssignmentAsync(
                new PolicyAssignmentSnapshot("assignment-1", "scope-local", "viewer@example.com", ["operator.viewer", "operator.viewer"], nowUtc, "keycloak", true),
                TestContext.Current.CancellationToken);

            var scope = Assert.Single(await store.ListScopesAsync(TestContext.Current.CancellationToken));
            var assignment = Assert.Single(await store.ListAssignmentsAsync(TestContext.Current.CancellationToken));

            Assert.Equal("scope-local", scope.ScopeId);
            Assert.Equal("tenant-a", scope.TenantId);
            Assert.Equal("org-a", scope.OrganizationId);
            Assert.True(scope.AdminOverrideEnabled);
            Assert.False(scope.IsActive);
            Assert.Equal("viewer@example.com", assignment.PrincipalId);
            Assert.Equal(["operator.viewer"], assignment.Roles);
            Assert.Equal("keycloak", assignment.Source);
            Assert.True(assignment.IsActive);

            await store.RemoveAssignmentAsync("assignment-1", TestContext.Current.CancellationToken);
            Assert.Empty(await store.ListAssignmentsAsync(TestContext.Current.CancellationToken));

            await store.RemoveScopeAsync("scope-local", TestContext.Current.CancellationToken);
            Assert.Empty(await store.ListScopesAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }
}
