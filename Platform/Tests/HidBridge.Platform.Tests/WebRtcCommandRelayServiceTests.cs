using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies WebRTC relay peer presence and command/ack flow.
/// </summary>
public sealed class WebRtcCommandRelayServiceTests
{
    [Fact]
    public async Task GetSessionMetricsAsync_ReturnsDeterministicDefaultsForUnknownSession()
    {
        var service = new WebRtcCommandRelayService();
        var cancellationToken = TestContext.Current.CancellationToken;

        var metrics = await service.GetSessionMetricsAsync("missing-session", "endpoint-1", cancellationToken);

        Assert.Equal("0", metrics["onlinePeerCount"]?.ToString());
        Assert.Equal("0", metrics["pendingAckCount"]?.ToString());
        Assert.Equal("0", metrics["queuedCommandCount"]?.ToString());
        Assert.Null(metrics["lastRelayAckAtUtc"]);
        Assert.Null(metrics["lastPeerSeenAtUtc"]);
        Assert.Null(metrics["lastPeerState"]);
        Assert.Null(metrics["lastPeerFailureReason"]);
        Assert.Null(metrics["lastPeerConsecutiveFailures"]);
        Assert.Null(metrics["lastPeerReconnectBackoffMs"]);
    }

    [Fact]
    public async Task PeerPresence_RoundtripOnlineOfflineAndMetrics()
    {
        var service = new WebRtcCommandRelayService();
        var cancellationToken = TestContext.Current.CancellationToken;

        var online = await service.MarkPeerOnlineAsync(
            "session-1",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string> { ["source"] = "browser" },
            cancellationToken);
        var peers = await service.ListPeersAsync("session-1", cancellationToken);
        var metrics = await service.GetSessionMetricsAsync("session-1", "endpoint-1", cancellationToken);

        Assert.True(online.IsOnline);
        Assert.Single(peers);
        Assert.True(service.HasOnlinePeer("session-1", "endpoint-1"));
        Assert.Equal("1", metrics["onlinePeerCount"]?.ToString());

        var offline = await service.MarkPeerOfflineAsync("session-1", "peer-1", cancellationToken);
        Assert.False(offline.IsOnline);
        Assert.False(service.HasOnlinePeer("session-1", "endpoint-1"));
    }

    [Fact]
    public async Task PeerPresence_MetricsIncludeLatestPeerDiagnostics()
    {
        var service = new WebRtcCommandRelayService();
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await service.MarkPeerOnlineAsync(
            "session-1",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string>
            {
                ["state"] = "Connected",
                ["failureReason"] = "",
                ["consecutiveFailures"] = "2",
                ["reconnectBackoffMs"] = "1500",
            },
            cancellationToken);

        var metrics = await service.GetSessionMetricsAsync("session-1", "endpoint-1", cancellationToken);

        Assert.Equal("Connected", metrics["lastPeerState"]?.ToString());
        Assert.Equal("2", metrics["lastPeerConsecutiveFailures"]?.ToString());
        Assert.Equal("1500", metrics["lastPeerReconnectBackoffMs"]?.ToString());
        Assert.NotNull(metrics["lastPeerSeenAtUtc"]);
    }

    [Fact]
    public async Task EnqueueAndAckCommand_CompletesPendingAck()
    {
        var service = new WebRtcCommandRelayService();
        var cancellationToken = TestContext.Current.CancellationToken;
        _ = await service.MarkPeerOnlineAsync("session-1", "peer-1", "endpoint-1", null, cancellationToken);
        var route = new RealtimeTransportRouteContext("agent-1", "endpoint-1", "session-1");
        var command = new CommandRequestBody(
            CommandId: "cmd-1",
            SessionId: "session-1",
            Channel: CommandChannel.DataChannel,
            Action: "keyboard.text",
            Args: new Dictionary<string, object?> { ["text"] = "hello" },
            TimeoutMs: 500,
            IdempotencyKey: "idem-1");

        var envelope = await service.EnqueueCommandAsync(route, command, cancellationToken);
        var queued = await service.ListCommandsAsync("session-1", "peer-1", null, 10, cancellationToken);
        var waitTask = service.WaitForAckAsync("session-1", "cmd-1", TimeSpan.FromSeconds(1), cancellationToken);
        var accepted = await service.PublishAckAsync(
            "session-1",
            new CommandAckBody("cmd-1", CommandStatus.Applied),
            cancellationToken);
        var ack = await waitTask;

        Assert.Equal(1, envelope.Sequence);
        Assert.Single(queued);
        Assert.True(accepted);
        Assert.NotNull(ack);
        Assert.Equal(CommandStatus.Applied, ack!.Status);
    }
}
