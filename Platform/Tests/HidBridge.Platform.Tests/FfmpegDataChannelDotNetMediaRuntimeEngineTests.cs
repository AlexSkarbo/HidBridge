using HidBridge.EdgeProxy.Agent.Media;
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
}
