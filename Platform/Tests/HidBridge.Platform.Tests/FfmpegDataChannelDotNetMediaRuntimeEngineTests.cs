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
}
