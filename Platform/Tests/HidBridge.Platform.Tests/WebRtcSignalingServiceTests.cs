using HidBridge.ControlPlane.Api;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies in-memory WebRTC signaling contract behavior.
/// </summary>
public sealed class WebRtcSignalingServiceTests
{
    [Fact]
    public async Task AppendAsync_AssignsIncreasingSequencePerSession()
    {
        var service = new WebRtcSignalingService();

        var first = await service.AppendAsync(
            sessionId: "session-1",
            kind: WebRtcSignalKind.Offer,
            senderPeerId: "peer-a",
            recipientPeerId: "peer-b",
            payload: "offer-sdp",
            mid: null,
            mLineIndex: null,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await service.AppendAsync(
            sessionId: "session-1",
            kind: WebRtcSignalKind.Answer,
            senderPeerId: "peer-b",
            recipientPeerId: "peer-a",
            payload: "answer-sdp",
            mid: null,
            mLineIndex: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
    }

    [Fact]
    public async Task ListAsync_FiltersByRecipientAndAfterSequence()
    {
        var service = new WebRtcSignalingService();
        await service.AppendAsync("session-1", WebRtcSignalKind.Offer, "peer-a", "peer-b", "offer-1", null, null, TestContext.Current.CancellationToken);
        await service.AppendAsync("session-1", WebRtcSignalKind.IceCandidate, "peer-a", "peer-b", "cand-1", "0", 0, TestContext.Current.CancellationToken);
        await service.AppendAsync("session-1", WebRtcSignalKind.Answer, "peer-b", "peer-a", "answer-1", null, null, TestContext.Current.CancellationToken);
        await service.AppendAsync("session-1", WebRtcSignalKind.Heartbeat, "peer-a", null, "hb-1", null, null, TestContext.Current.CancellationToken);

        var peerB = await service.ListAsync(
            sessionId: "session-1",
            recipientPeerId: "peer-b",
            afterSequence: null,
            limit: 100,
            cancellationToken: TestContext.Current.CancellationToken);
        var afterTwo = await service.ListAsync(
            sessionId: "session-1",
            recipientPeerId: null,
            afterSequence: 2,
            limit: 100,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, peerB.Count);
        Assert.All(peerB, item => Assert.True(string.IsNullOrWhiteSpace(item.RecipientPeerId) || item.RecipientPeerId == "peer-b"));
        Assert.Equal(2, afterTwo.Count);
        Assert.All(afterTwo, item => Assert.True(item.Sequence > 2));
    }
}
