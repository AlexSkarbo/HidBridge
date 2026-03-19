using HidBridge.Application;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies media stream registry behavior used by edge-proxy media path.
/// </summary>
public sealed class SessionMediaRegistryServiceTests
{
    /// <summary>
    /// Upserts one media stream and returns the latest snapshot for endpoint.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_StoresSnapshot_AndReturnsLatestByEndpoint()
    {
        var service = new SessionMediaRegistryService();
        var nowUtc = DateTimeOffset.UtcNow;

        var snapshot = await service.UpsertAsync(
            "session-1",
            new SessionMediaStreamRegistrationBody(
                PeerId: "peer-1",
                EndpointId: "endpoint-1",
                StreamId: "stream-main",
                Ready: true,
                State: "Ready",
                ReportedAtUtc: nowUtc,
                Source: "hdmi-usb-capture"),
            TestContext.Current.CancellationToken);

        Assert.Equal("session-1", snapshot.SessionId);
        Assert.True(snapshot.Ready);
        Assert.Equal("Ready", snapshot.State);
        Assert.Equal("stream-main", snapshot.StreamId);

        var latest = await service.GetLatestAsync("session-1", "endpoint-1", TestContext.Current.CancellationToken);
        Assert.NotNull(latest);
        Assert.Equal("stream-main", latest!.StreamId);
        Assert.Equal("hdmi-usb-capture", latest.Source);
    }

    /// <summary>
    /// Lists media streams with endpoint filter and sorts by reported timestamp descending.
    /// </summary>
    [Fact]
    public async Task ListAsync_FiltersByEndpoint_AndSortsByReportedAtDescending()
    {
        var service = new SessionMediaRegistryService();
        var older = DateTimeOffset.UtcNow.AddSeconds(-10);
        var newer = DateTimeOffset.UtcNow;

        _ = await service.UpsertAsync(
            "session-2",
            new SessionMediaStreamRegistrationBody(
                PeerId: "peer-a",
                EndpointId: "endpoint-a",
                StreamId: "stream-old",
                Ready: true,
                State: "Ready",
                ReportedAtUtc: older),
            TestContext.Current.CancellationToken);
        _ = await service.UpsertAsync(
            "session-2",
            new SessionMediaStreamRegistrationBody(
                PeerId: "peer-b",
                EndpointId: "endpoint-a",
                StreamId: "stream-new",
                Ready: false,
                State: "Degraded",
                ReportedAtUtc: newer,
                FailureReason: "capture lag"),
            TestContext.Current.CancellationToken);
        _ = await service.UpsertAsync(
            "session-2",
            new SessionMediaStreamRegistrationBody(
                PeerId: "peer-c",
                EndpointId: "endpoint-other",
                StreamId: "stream-other",
                Ready: true,
                State: "Ready",
                ReportedAtUtc: newer),
            TestContext.Current.CancellationToken);

        var items = await service.ListAsync("session-2", peerId: null, endpointId: "endpoint-a", TestContext.Current.CancellationToken);
        Assert.Equal(2, items.Count);
        Assert.Equal("stream-new", items[0].StreamId);
        Assert.Equal("stream-old", items[1].StreamId);
        Assert.Equal("capture lag", items[0].FailureReason);
    }
}
