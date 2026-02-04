using HidControl.Contracts;
using HidControlServer;
using static HidControlServer.ServerUtils;
using Xunit;

namespace HidControlServer.Tests;

/// <summary>
/// Tests ServerUtilsTests.
/// </summary>
public sealed class ServerUtilsTests
{
    [Fact]
    /// <summary>
    /// Builds bind urls bind all false returns original.
    /// </summary>
    public void BuildBindUrls_BindAllFalse_ReturnsOriginal()
    {
        var opt = new Options(
            "COM1", false, null, 115200, "http://127.0.0.1:8080", false, "db",
            "Host=localhost", false, Array.Empty<VideoSourceConfig>(),
            Array.Empty<VideoProfileConfig>(), "low-latency",
            4, 256, 8, 200, 2, 50, true, true, 50, true, true, 200, 2,
            "secret", 255, 254, "mouse", "keyboard", true, 2000, true, "",
            "", 1, 2, 4, 8, 16, null, "ffmpeg",
            false, true, 1000, 1500, 10, 60000,
            8554, 9000, 50, 1935, "video_hls", true, 1, 6, true, "video_logs",
            true, 3, 500, 2000, false, true, 256 * 1024, 64 * 1024, 16 * 1024, 4 * 1024 * 1024, 300, 600);

        string[] urls = BuildBindUrls(opt);

        Assert.Single(urls);
        Assert.Equal("http://127.0.0.1:8080", urls[0]);
    }

    [Fact]
    /// <summary>
    /// Builds bind urls bind all true with scheme returns wildcard and original.
    /// </summary>
    public void BuildBindUrls_BindAllTrue_WithScheme_ReturnsWildcardAndOriginal()
    {
        var opt = new Options(
            "COM1", false, null, 115200, "http://127.0.0.1:8080", true, "db",
            "Host=localhost", false, Array.Empty<VideoSourceConfig>(),
            Array.Empty<VideoProfileConfig>(), "low-latency",
            4, 256, 8, 200, 2, 50, true, true, 50, true, true, 200, 2,
            "secret", 255, 254, "mouse", "keyboard", true, 2000, true, "",
            "", 1, 2, 4, 8, 16, null, "ffmpeg",
            false, true, 1000, 1500, 10, 60000,
            8554, 9000, 50, 1935, "video_hls", true, 1, 6, true, "video_logs",
            true, 3, 500, 2000, false, true, 256 * 1024, 64 * 1024, 16 * 1024, 4 * 1024 * 1024, 300, 600);

        string[] urls = BuildBindUrls(opt);

        Assert.Equal(2, urls.Length);
        Assert.Contains("http://0.0.0.0:8080", urls);
        Assert.Contains("http://127.0.0.1:8080", urls);
    }

    [Fact]
    /// <summary>
    /// Executes InferTypeName_UsesInferredOrProtocol.
    /// </summary>
    public void InferTypeName_UsesInferredOrProtocol()
    {
        Assert.Equal("mouse", InferTypeName(2, 0));
        Assert.Equal("mouse", InferTypeName(0, 2));
        Assert.Equal("keyboard", InferTypeName(1, 0));
        Assert.Equal("keyboard", InferTypeName(0, 1));
        Assert.Equal("unknown", InferTypeName(0, 0));
    }

    [Fact]
    /// <summary>
    /// Builds device id uses type and hash.
    /// </summary>
    public void BuildDeviceId_UsesTypeAndHash()
    {
        string? id = BuildDeviceId(2, 0, "ABC");
        Assert.Equal("mouse:ABC", id);

        string? none = BuildDeviceId(1, 0, null);
        Assert.Null(none);
    }

    [Fact]
    /// <summary>
    /// Parses button n mask parses valid range.
    /// </summary>
    public void ParseButtonNMask_ParsesValidRange()
    {
        Assert.Equal(1, ParseButtonNMask("b0"));
        Assert.Equal(2, ParseButtonNMask("b1"));
        Assert.Equal(128, ParseButtonNMask("b7"));
        Assert.Equal(0, ParseButtonNMask("b8"));
        Assert.Equal(0, ParseButtonNMask("x1"));
    }

    [Fact]
    /// <summary>
    /// Executes ComputeReportDescHash_ReturnsHexSha256.
    /// </summary>
    public void ComputeReportDescHash_ReturnsHexSha256()
    {
        string hash = ComputeReportDescHash(new byte[] { 1, 2, 3 });
        Assert.Equal(64, hash.Length);
    }
}
