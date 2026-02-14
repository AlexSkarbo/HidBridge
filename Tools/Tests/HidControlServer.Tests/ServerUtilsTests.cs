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
        var opt = Options.Parse(new[]
        {
            "--url", "http://127.0.0.1:8080",
            "--bindAll", "false"
        });

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
        var opt = Options.Parse(new[]
        {
            "--url", "http://127.0.0.1:8080",
            "--bindAll", "true"
        });

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

    [Fact]
    /// <summary>
    /// Parses webrtc video helper source options from config.
    /// </summary>
    public void Parse_Config_WebRtcVideoPeerSourceOptions_AreLoaded()
    {
        string cfgPath = Path.Combine(Path.GetTempPath(), $"hidcontrol_webrtc_video_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(cfgPath, """
            {
              "webRtcVideoPeerSourceMode": "capture",
              "webRtcVideoPeerCaptureInput": "-f dshow -i video=USB3.0 Video",
              "webRtcVideoPeerFfmpegArgs": "-f lavfi -i testsrc=size=640x360:rate=30 -an -c:v libvpx -deadline realtime -cpu-used 8 -g 60 -b:v 1200k -pix_fmt yuv420p"
            }
            """);

            var opt = Options.Parse(new[] { "--config", cfgPath });
            Assert.Equal("capture", opt.WebRtcVideoPeerSourceMode);
            Assert.Equal("-f dshow -i video=USB3.0 Video", opt.WebRtcVideoPeerCaptureInput);
            Assert.Contains("-f lavfi", opt.WebRtcVideoPeerFfmpegArgs, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(cfgPath); } catch { }
        }
    }

    [Fact]
    /// <summary>
    /// Uses default webrtc video helper source mode when config omits it.
    /// </summary>
    public void Parse_Config_WebRtcVideoPeerSourceMode_DefaultsToTestsrc()
    {
        string cfgPath = Path.Combine(Path.GetTempPath(), $"hidcontrol_webrtc_video_default_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(cfgPath, "{}");
            var opt = Options.Parse(new[] { "--config", cfgPath });
            Assert.Equal("testsrc", opt.WebRtcVideoPeerSourceMode);
            Assert.Null(opt.WebRtcVideoPeerCaptureInput);
            Assert.Null(opt.WebRtcVideoPeerFfmpegArgs);
        }
        finally
        {
            try { File.Delete(cfgPath); } catch { }
        }
    }
}
