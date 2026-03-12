using HidBridge.Abstractions;
using HidBridge.Contracts;
using HidBridge.Persistence;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies the file-backed persistence baseline introduced in P0-3.
/// </summary>
public sealed class FilePersistenceStoreTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), "hidbridge-platform-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Cleans up the temporary persistence directory after each test.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [Fact]
    /// <summary>
    /// Verifies that connector and endpoint snapshots survive reloading the file stores.
    /// </summary>
    public async Task ConnectorAndEndpointStores_PersistSnapshotsAcrossInstances()
    {
        var options = new FilePersistenceOptions(_rootDirectory);
        var connectorStore = new FileConnectorCatalogStore(options);
        var endpointStore = new FileEndpointSnapshotStore(options);

        await connectorStore.UpsertAsync(
            new ConnectorDescriptor(
                "agent-1",
                "endpoint-1",
                ConnectorType.HidBridge,
                new[] { new CapabilityDescriptor(CapabilityNames.HidMouseV1, "1.0") }),
            TestContext.Current.CancellationToken);

        await endpointStore.UpsertAsync(
            new EndpointSnapshot(
                "endpoint-1",
                new[] { new CapabilityDescriptor(CapabilityNames.HidMouseV1, "1.0") },
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        var connectorReload = new FileConnectorCatalogStore(options);
        var endpointReload = new FileEndpointSnapshotStore(options);

        var connectors = await connectorReload.ListAsync(TestContext.Current.CancellationToken);
        var endpoints = await endpointReload.ListAsync(TestContext.Current.CancellationToken);

        Assert.Single(connectors);
        Assert.Single(endpoints);
        Assert.Equal("agent-1", connectors[0].AgentId);
        Assert.Equal(ConnectorType.HidBridge, connectors[0].ConnectorType);
        Assert.Equal("endpoint-1", endpoints[0].EndpointId);
        Assert.Contains(endpoints[0].Capabilities, x => x.Name == CapabilityNames.HidMouseV1);
    }

    [Fact]
    /// <summary>
    /// Verifies that audit and telemetry events are appended and readable from disk.
    /// </summary>
    public async Task EventStore_AppendsAndReadsAuditAndTelemetry()
    {
        var store = new FileEventStore(new FilePersistenceOptions(_rootDirectory));

        await store.AppendAuditAsync(
            new AuditEventBody("session", "opened", "session-1"),
            TestContext.Current.CancellationToken);
        await store.AppendTelemetryAsync(
            new TelemetryEventBody("command", new Dictionary<string, object?> { ["status"] = "Applied" }, "session-1"),
            TestContext.Current.CancellationToken);

        var audit = await store.ListAuditAsync(TestContext.Current.CancellationToken);
        var telemetry = await store.ListTelemetryAsync(TestContext.Current.CancellationToken);

        Assert.Single(audit);
        Assert.Single(telemetry);
        Assert.Equal("opened", audit[0].Message);
        Assert.Equal("session-1", telemetry[0].SessionId);
        Assert.True(telemetry[0].CreatedAtUtc.HasValue);
        Assert.Equal("Applied", telemetry[0].Metrics["status"]?.ToString());
    }

    [Fact]
    /// <summary>
    /// Verifies that numeric telemetry metrics survive a full file-backed roundtrip.
    /// </summary>
    public async Task EventStore_PreservesNumericTelemetryMetricsAcrossReload()
    {
        var options = new FilePersistenceOptions(_rootDirectory);
        var store = new FileEventStore(options);

        await store.AppendTelemetryAsync(
            new TelemetryEventBody("stream", new Dictionary<string, object?> { ["fps"] = 25, ["latencyMs"] = 75.5 }, "session-2"),
            TestContext.Current.CancellationToken);

        var reloadedStore = new FileEventStore(options);
        var telemetry = await reloadedStore.ListTelemetryAsync(TestContext.Current.CancellationToken);

        Assert.Single(telemetry);
        Assert.Equal("25", telemetry[0].Metrics["fps"]?.ToString());
        Assert.Equal("75.5", telemetry[0].Metrics["latencyMs"]?.ToString());
    }

    [Fact]
    /// <summary>
    /// Verifies that session snapshots are written and removed from persistent storage.
    /// </summary>
    public async Task SessionStore_UpsertAndRemoveRoundTrips()
    {
        var store = new FileSessionStore(new FilePersistenceOptions(_rootDirectory));
        var snapshot = new SessionSnapshot(
            "session-1",
            "agent-1",
            "endpoint-1",
            SessionProfile.UltraLowLatency,
            "tester",
            SessionRole.Owner,
            SessionState.Active,
            DateTimeOffset.UtcNow);

        await store.UpsertAsync(snapshot, TestContext.Current.CancellationToken);
        var afterInsert = await store.ListAsync(TestContext.Current.CancellationToken);
        Assert.Single(afterInsert);

        await store.RemoveAsync("session-1", TestContext.Current.CancellationToken);
        var afterDelete = await store.ListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(afterDelete);
    }
}
