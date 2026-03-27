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
        Assert.Null(metrics["lastPeerMediaReady"]);
        Assert.Null(metrics["lastPeerMediaState"]);
        Assert.Null(metrics["lastPeerMediaFailureReason"]);
        Assert.Null(metrics["lastPeerMediaReportedAtUtc"]);
        Assert.Null(metrics["lastPeerMediaStreamId"]);
        Assert.Null(metrics["lastPeerMediaSource"]);
        Assert.Null(metrics["lastPeerMediaPlaybackUrl"]);
        Assert.Null(metrics["lastPeerMediaStreamKind"]);
        Assert.Null(metrics["lastPeerMediaVideoCodec"]);
        Assert.Null(metrics["lastPeerMediaVideoWidth"]);
        Assert.Null(metrics["lastPeerMediaVideoHeight"]);
        Assert.Null(metrics["lastPeerMediaVideoFrameRate"]);
        Assert.Null(metrics["lastPeerMediaVideoBitrateKbps"]);
        Assert.Null(metrics["lastPeerMediaAudioCodec"]);
        Assert.Null(metrics["lastPeerMediaAudioChannels"]);
        Assert.Null(metrics["lastPeerMediaAudioSampleRateHz"]);
        Assert.Null(metrics["lastPeerMediaAudioBitrateKbps"]);
        Assert.Null(metrics["lastPeerMediaSessionEvidence"]);
        Assert.Null(metrics["lastPeerMediaRuntimeSessionEvidence"]);
        Assert.Null(metrics["lastPeerMediaSessionState"]);
        Assert.Null(metrics["lastPeerMediaVideoTrackState"]);
        Assert.Null(metrics["lastPeerMediaAudioTrackState"]);
        Assert.Null(metrics["lastPeerMediaSessionObservedAtUtc"]);
        Assert.Null(metrics["lastPeerMediaBackendAutoStart"]);
        Assert.Null(metrics["lastPeerMediaBackendRunning"]);
        Assert.Null(metrics["lastPeerMediaBackendState"]);
        Assert.Null(metrics["lastPeerMediaBackendReportedAtUtc"]);
        Assert.Null(metrics["lastPeerMediaBackendPid"]);
        Assert.Null(metrics["lastPeerMediaBackendUptimeSec"]);
        Assert.Null(metrics["lastPeerMediaBackendLastLine"]);
        Assert.Null(metrics["lastPeerMediaBackendExecutable"]);
        Assert.Null(metrics["lastPeerMediaBackendWorkingDirectory"]);
        Assert.Null(metrics["lastPeerMediaBackendProbeEndpoints"]);
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
                ["mediaReady"] = "true",
                ["mediaState"] = "Streaming",
                ["mediaReportedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["mediaStreamId"] = "stream-main",
                ["mediaSource"] = "hdmi-usb-capture",
                ["mediaPlaybackUrl"] = "http://127.0.0.1:8080/live.m3u8",
                ["mediaStreamKind"] = "audio-video",
                ["mediaVideoCodec"] = "h264",
                ["mediaVideoWidth"] = "1920",
                ["mediaVideoHeight"] = "1080",
                ["mediaVideoFrameRate"] = "30",
                ["mediaVideoBitrateKbps"] = "4000",
                ["mediaAudioCodec"] = "opus",
                ["mediaAudioChannels"] = "2",
                ["mediaAudioSampleRateHz"] = "48000",
                ["mediaAudioBitrateKbps"] = "128",
                ["mediaSessionEvidence"] = "true",
                ["mediaRuntimeSessionEvidence"] = "true",
                ["mediaSessionState"] = "active",
                ["mediaVideoTrackState"] = "active",
                ["mediaAudioTrackState"] = "active",
                ["mediaSessionObservedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["mediaBackendAutoStart"] = "true",
                ["mediaBackendRunning"] = "true",
                ["mediaBackendState"] = "running",
                ["mediaBackendReportedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["mediaBackendPid"] = "45400",
                ["mediaBackendUptimeSec"] = "22.5",
                ["mediaBackendLastLine"] = "backend-started",
                ["mediaBackendExecutable"] = "srs.exe",
                ["mediaBackendWorkingDirectory"] = "C:/media",
                ["mediaBackendProbeEndpoints"] = "http://127.0.0.1:19851, http://127.0.0.1:28092",
            },
            cancellationToken);

        var metrics = await service.GetSessionMetricsAsync("session-1", "endpoint-1", cancellationToken);

        Assert.Equal("Connected", metrics["lastPeerState"]?.ToString());
        Assert.Equal("2", metrics["lastPeerConsecutiveFailures"]?.ToString());
        Assert.Equal("1500", metrics["lastPeerReconnectBackoffMs"]?.ToString());
        Assert.Equal("true", metrics["lastPeerMediaReady"]?.ToString());
        Assert.Equal("Streaming", metrics["lastPeerMediaState"]?.ToString());
        Assert.Equal("stream-main", metrics["lastPeerMediaStreamId"]?.ToString());
        Assert.Equal("hdmi-usb-capture", metrics["lastPeerMediaSource"]?.ToString());
        Assert.Equal("http://127.0.0.1:8080/live.m3u8", metrics["lastPeerMediaPlaybackUrl"]?.ToString());
        Assert.Equal("audio-video", metrics["lastPeerMediaStreamKind"]?.ToString());
        Assert.Equal("h264", metrics["lastPeerMediaVideoCodec"]?.ToString());
        Assert.Equal("1920", metrics["lastPeerMediaVideoWidth"]?.ToString());
        Assert.Equal("1080", metrics["lastPeerMediaVideoHeight"]?.ToString());
        Assert.Equal("30", metrics["lastPeerMediaVideoFrameRate"]?.ToString());
        Assert.Equal("4000", metrics["lastPeerMediaVideoBitrateKbps"]?.ToString());
        Assert.Equal("opus", metrics["lastPeerMediaAudioCodec"]?.ToString());
        Assert.Equal("2", metrics["lastPeerMediaAudioChannels"]?.ToString());
        Assert.Equal("48000", metrics["lastPeerMediaAudioSampleRateHz"]?.ToString());
        Assert.Equal("128", metrics["lastPeerMediaAudioBitrateKbps"]?.ToString());
        Assert.Equal("true", metrics["lastPeerMediaSessionEvidence"]?.ToString());
        Assert.Equal("true", metrics["lastPeerMediaRuntimeSessionEvidence"]?.ToString());
        Assert.Equal("active", metrics["lastPeerMediaSessionState"]?.ToString());
        Assert.Equal("active", metrics["lastPeerMediaVideoTrackState"]?.ToString());
        Assert.Equal("active", metrics["lastPeerMediaAudioTrackState"]?.ToString());
        Assert.Equal("true", metrics["lastPeerMediaBackendAutoStart"]?.ToString());
        Assert.Equal("true", metrics["lastPeerMediaBackendRunning"]?.ToString());
        Assert.Equal("running", metrics["lastPeerMediaBackendState"]?.ToString());
        Assert.Equal("45400", metrics["lastPeerMediaBackendPid"]?.ToString());
        Assert.Equal("22.5", metrics["lastPeerMediaBackendUptimeSec"]?.ToString());
        Assert.Equal("backend-started", metrics["lastPeerMediaBackendLastLine"]?.ToString());
        Assert.Equal("srs.exe", metrics["lastPeerMediaBackendExecutable"]?.ToString());
        Assert.Equal("C:/media", metrics["lastPeerMediaBackendWorkingDirectory"]?.ToString());
        Assert.Equal("http://127.0.0.1:19851, http://127.0.0.1:28092", metrics["lastPeerMediaBackendProbeEndpoints"]?.ToString());
        Assert.NotNull(metrics["lastPeerSeenAtUtc"]);
        Assert.NotNull(metrics["lastPeerMediaReportedAtUtc"]);
        Assert.NotNull(metrics["lastPeerMediaSessionObservedAtUtc"]);
        Assert.NotNull(metrics["lastPeerMediaBackendReportedAtUtc"]);
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

    [Fact]
    public async Task HasOnlinePeer_ReturnsFalse_WhenPeerHeartbeatIsStale()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var service = new WebRtcCommandRelayService(
            new WebRtcCommandRelayOptions
            {
                PeerStaleAfterSec = 5,
            },
            clock: () => nowUtc);
        var cancellationToken = TestContext.Current.CancellationToken;

        _ = await service.MarkPeerOnlineAsync(
            "session-stale",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string> { ["state"] = "Connected" },
            cancellationToken);

        Assert.True(service.HasOnlinePeer("session-stale", "endpoint-1"));

        nowUtc = nowUtc.AddSeconds(7);
        Assert.False(service.HasOnlinePeer("session-stale", "endpoint-1"));

        var metrics = await service.GetSessionMetricsAsync("session-stale", "endpoint-1", cancellationToken);
        Assert.Equal("0", metrics["onlinePeerCount"]?.ToString());
        Assert.Equal("Stale", metrics["lastPeerState"]?.ToString());
        Assert.Equal("peer_stale_timeout", metrics["lastPeerFailureReason"]?.ToString());
        Assert.Equal("True", metrics["lastPeerIsStale"]?.ToString());
    }
}
