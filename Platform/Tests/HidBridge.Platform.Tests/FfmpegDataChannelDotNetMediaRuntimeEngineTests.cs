using HidBridge.EdgeProxy.Agent.Media;
using HidBridge.EdgeProxy.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace HidBridge.Platform.Tests;

public sealed class FfmpegDataChannelDotNetMediaRuntimeEngineTests
{
    [Fact]
    public void TryParseProgressMetrics_ParsesTypicalFfmpegStatusLine()
    {
        const string line = "frame=  245 fps=29.9 q=28.0 size=    8192kB time=00:00:08.16 bitrate=8224.0kbits/s speed=1.01x dup=1 drop=2";

        var parsed = FfmpegDataChannelDotNetMediaRuntimeEngine.TryParseProgressMetrics(line, out var metrics);

        Assert.True(parsed);
        Assert.Equal(245, metrics["ffmpegFrame"]);
        Assert.Equal(29.9d, Assert.IsType<double>(metrics["ffmpegFps"]), 6);
        Assert.Equal(1.01d, Assert.IsType<double>(metrics["ffmpegSpeed"]), 6);
        Assert.Equal(1, metrics["ffmpegDupFrames"]);
        Assert.Equal(2, metrics["ffmpegDropFrames"]);
        Assert.Equal(8.16d, Assert.IsType<double>(metrics["ffmpegTimeSec"]), 6);
        Assert.Equal(8224d, Assert.IsType<double>(metrics["ffmpegBitrateKbps"]), 6);
    }

    [Fact]
    public void TryParseProgressMetrics_ReturnsFalseForNonProgressLine()
    {
        var parsed = FfmpegDataChannelDotNetMediaRuntimeEngine.TryParseProgressMetrics(
            "[h264 @ 000001] Opening stream",
            out var metrics);

        Assert.False(parsed);
        Assert.Empty(metrics);
    }

    [Fact]
    public void TryClassifyRuntimeErrorLine_DetectsTransportAndHttpFailures()
    {
        var classified = FfmpegDataChannelDotNetMediaRuntimeEngine.TryClassifyRuntimeErrorLine(
            "av_interleaved_write_frame(): Connection refused",
            out var reason);

        Assert.True(classified);
        Assert.Contains("connection refused", reason, StringComparison.OrdinalIgnoreCase);

        classified = FfmpegDataChannelDotNetMediaRuntimeEngine.TryClassifyRuntimeErrorLine(
            "Server returned 404 Not Found",
            out reason);

        Assert.True(classified);
        Assert.Contains("rejected request", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryClassifyRuntimeErrorLine_IgnoresNeutralLine()
    {
        var classified = FfmpegDataChannelDotNetMediaRuntimeEngine.TryClassifyRuntimeErrorLine(
            "[libx264 @ 000001] profile High, level 3.1",
            out var reason);

        Assert.False(classified);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void TryDetectTrackDescriptor_DetectsVideoAndAudioDescriptors()
    {
        var detected = FfmpegDataChannelDotNetMediaRuntimeEngine.TryDetectTrackDescriptor(
            "Stream #0:0: Video: h264 (High), yuv420p, 1280x720",
            out var hasVideo,
            out var hasAudio);

        Assert.True(detected);
        Assert.True(hasVideo);
        Assert.False(hasAudio);

        detected = FfmpegDataChannelDotNetMediaRuntimeEngine.TryDetectTrackDescriptor(
            "Stream #0:1: Audio: aac (LC), 48000 Hz, stereo",
            out hasVideo,
            out hasAudio);

        Assert.True(detected);
        Assert.False(hasVideo);
        Assert.True(hasAudio);
    }

    [Fact]
    public void TryBuildRtmpPublishUrlFromWhip_MapsKnownSrsPortsAndQuery()
    {
        var mapped = FfmpegDataChannelDotNetMediaRuntimeEngine.TryBuildRtmpPublishUrlFromWhip(
            "http://127.0.0.1:19851/rtc/v1/whip/?app=live&stream=cam21",
            "edge-main",
            out var rtmpUrl);

        Assert.True(mapped);
        Assert.Equal("rtmp://127.0.0.1:19351/live/cam21", rtmpUrl);
    }

    [Fact]
    public void TryBuildRtmpPublishUrlFromWhip_UsesFallbackStreamWhenQueryIsMissing()
    {
        var mapped = FfmpegDataChannelDotNetMediaRuntimeEngine.TryBuildRtmpPublishUrlFromWhip(
            "http://localhost:19851/rtc/v1/whip/",
            "edge-main",
            out var rtmpUrl);

        Assert.True(mapped);
        Assert.Equal("rtmp://localhost:19351/live/edge-main", rtmpUrl);
    }

    [Fact]
    public async Task StartAsync_AutoStartEnabled_NoBackendExecutable_ReportsMediaBackendNotConfigured()
    {
        var port = GetFreeTcpPort();
        var options = CreateBaseOptions();
        options.MediaBackendAutoStart = true;
        options.MediaBackendExecutablePath = string.Empty;
        options.MediaWhipUrl = $"http://127.0.0.1:{port}/rtc/v1/whip/?app=live&stream=test";
        options.MediaWhepUrl = $"http://127.0.0.1:{port}/rtc/v1/whep/?app=live&stream=test";
        options.Normalize();

        var engine = new FfmpegDataChannelDotNetMediaRuntimeEngine(options, NullLogger.Instance);
        await engine.StartAsync(CreateContext(), CancellationToken.None);
        var snapshot = await engine.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("MediaBackendNotConfigured", snapshot.State);
        Assert.Contains("Media backend is unreachable", snapshot.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_AutoStartEnabled_BackendExitsDuringStartup_ReportsStartFailed()
    {
        var port = GetFreeTcpPort();
        var options = CreateBaseOptions();
        options.MediaBackendAutoStart = true;
        options.MediaBackendExecutablePath = "dotnet";
        options.MediaBackendArgumentsTemplate = "--version";
        options.MediaBackendStartupTimeoutSec = 3;
        options.MediaBackendProbeDelayMs = 100;
        options.MediaBackendProbeTimeoutMs = 200;
        options.MediaWhipUrl = $"http://127.0.0.1:{port}/rtc/v1/whip/?app=live&stream=test";
        options.MediaWhepUrl = $"http://127.0.0.1:{port}/rtc/v1/whep/?app=live&stream=test";
        options.Normalize();

        var engine = new FfmpegDataChannelDotNetMediaRuntimeEngine(options, NullLogger.Instance);
        await engine.StartAsync(CreateContext(), CancellationToken.None);
        var snapshot = await engine.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("MediaBackendStartFailed", snapshot.State);
        Assert.Contains("exited during startup", snapshot.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_AutoStartEnabled_BackendUnreachableUntilTimeout_ReportsUnavailable()
    {
        if (!TryGetLongRunningCommand(out var executable, out var args))
        {
            return;
        }

        var port = GetFreeTcpPort();
        var options = CreateBaseOptions();
        options.MediaBackendAutoStart = true;
        options.MediaBackendExecutablePath = executable;
        options.MediaBackendArgumentsTemplate = args;
        options.MediaBackendStartupTimeoutSec = 1;
        options.MediaBackendProbeDelayMs = 100;
        options.MediaBackendProbeTimeoutMs = 200;
        options.MediaWhipUrl = $"http://127.0.0.1:{port}/rtc/v1/whip/?app=live&stream=test";
        options.MediaWhepUrl = $"http://127.0.0.1:{port}/rtc/v1/whep/?app=live&stream=test";
        options.Normalize();

        var engine = new FfmpegDataChannelDotNetMediaRuntimeEngine(options, NullLogger.Instance);
        await engine.StartAsync(CreateContext(), CancellationToken.None);
        var snapshot = await engine.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("MediaBackendUnavailable", snapshot.State);
        Assert.Contains("unreachable", snapshot.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_AutoStartEnabled_WhenEndpointAlreadyReachable_DoesNotBlockFfmpegStart()
    {
        if (!TryGetLongRunningCommand(out var executable, out var args))
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var options = CreateBaseOptions();
        options.MediaBackendAutoStart = true;
        options.MediaBackendExecutablePath = "definitely-not-used-when-endpoint-is-reachable";
        options.MediaBackendArgumentsTemplate = "--ignored";
        options.FfmpegExecutablePath = executable;
        options.FfmpegArgumentsTemplate = args;
        options.MediaWhipUrl = $"http://127.0.0.1:{port}/rtc/v1/whip/?app=live&stream=test";
        options.MediaWhepUrl = $"http://127.0.0.1:{port}/rtc/v1/whep/?app=live&stream=test";
        options.Normalize();

        var engine = new FfmpegDataChannelDotNetMediaRuntimeEngine(options, NullLogger.Instance);
        await engine.StartAsync(CreateContext(), CancellationToken.None);
        var snapshot = await engine.GetSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.IsRunning);
        Assert.Contains(snapshot.State, new[] { "Publishing", "Connecting", "Running", "Degraded" });

        await engine.StopAsync(CancellationToken.None);
    }

    private static EdgeProxyOptions CreateBaseOptions()
        => new()
        {
            BaseUrl = "http://127.0.0.1:18093",
            SessionId = "session-1",
            PeerId = "peer-1",
            EndpointId = "endpoint-1",
            CommandExecutor = "uart",
            UartPort = "COM6",
            MediaEngine = "ffmpeg-dcd",
            FfmpegExecutablePath = "ffmpeg",
            FfmpegArgumentsTemplate = "-re -f lavfi -i testsrc2=size=320x240:rate=30 -f null -",
        };

    private static EdgeMediaRuntimeContext CreateContext()
        => new(
            SessionId: "session-1",
            PeerId: "peer-1",
            EndpointId: "endpoint-1",
            StreamId: "edge-main",
            Source: "edge-capture");

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool TryGetLongRunningCommand(out string executable, out string args)
    {
        if (OperatingSystem.IsWindows())
        {
            executable = "ping";
            args = "-t 127.0.0.1";
            return true;
        }

        executable = "ping";
        args = "127.0.0.1";
        return true;
    }
}
