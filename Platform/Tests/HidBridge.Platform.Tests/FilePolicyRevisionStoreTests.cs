using HidBridge.Abstractions;
using HidBridge.Persistence;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies file-backed policy revision persistence.
/// </summary>
public sealed class FilePolicyRevisionStoreTests
{
    /// <summary>
    /// Verifies that appended revisions round-trip in descending timestamp order.
    /// </summary>
    [Fact]
    public async Task AppendAndListAsync_RoundTripsPolicyRevisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "hidbridge-policy-revisions", Guid.NewGuid().ToString("N"));
        var store = new FilePolicyRevisionStore(new FilePersistenceOptions(root));
        var nowUtc = DateTimeOffset.UtcNow;

        await store.AppendAsync(new PolicyRevisionSnapshot("rev-1", "scope", "scope-a", "scope-a", null, 1, "{}", nowUtc, "test"), TestContext.Current.CancellationToken);
        await store.AppendAsync(new PolicyRevisionSnapshot("rev-2", "assignment", "scope-a:user-a", "scope-a", "user-a", 1, "{}", nowUtc.AddMinutes(1), "test"), TestContext.Current.CancellationToken);

        var revisions = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, revisions.Count);
        Assert.Equal("rev-2", revisions[0].RevisionId);
        Assert.Equal("rev-1", revisions[1].RevisionId);
    }

    /// <summary>
    /// Verifies that retention keeps the newest revisions per entity while pruning stale history.
    /// </summary>
    [Fact]
    public async Task PruneAsync_RemovesStaleRevisionsOutsideRetentionWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "hidbridge-policy-revisions", Guid.NewGuid().ToString("N"));
        var store = new FilePolicyRevisionStore(new FilePersistenceOptions(root));
        var nowUtc = DateTimeOffset.UtcNow;

        await store.AppendAsync(new PolicyRevisionSnapshot("rev-1", "scope", "scope-a", "scope-a", null, 1, "{}", nowUtc.AddDays(-120), "test"), TestContext.Current.CancellationToken);
        await store.AppendAsync(new PolicyRevisionSnapshot("rev-2", "scope", "scope-a", "scope-a", null, 2, "{}", nowUtc.AddDays(-90), "test"), TestContext.Current.CancellationToken);
        await store.AppendAsync(new PolicyRevisionSnapshot("rev-3", "scope", "scope-a", "scope-a", null, 3, "{}", nowUtc.AddDays(-1), "test"), TestContext.Current.CancellationToken);

        var deleted = await store.PruneAsync(nowUtc.AddDays(-30), 1, TestContext.Current.CancellationToken);
        var revisions = await store.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        Assert.Single(revisions);
        Assert.Equal("rev-3", revisions[0].RevisionId);
    }
}
