using HidControl.Contracts;
using HidControlServer;
using static HidControlServer.ServerUtils;
using Xunit;

namespace HidControlServer.Tests;

/// <summary>
/// Tests VideoInputTests.
/// </summary>
public sealed class VideoInputTests
{
    [Fact]
    /// <summary>
    /// Builds ffmpeg input uvc linux path uses v 4 l 2.
    /// </summary>
    public void BuildFfmpegInput_UvcLinuxPath_UsesV4L2()
    {
        var src = new VideoSourceConfig("cam", "uvc", "/dev/video0", "USB", true);
        string? input = BuildFfmpegInput(src);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("-f v4l2 -i /dev/video0", input);
        }
        else
        {
            // Linux-only input format, should be rejected on non-Linux OS.
            Assert.Null(input);
        }
    }

    [Fact]
    /// <summary>
    /// Builds ffmpeg input uvc windows name uses dshow name.
    /// </summary>
    public void BuildFfmpegInput_UvcWindowsName_UsesDshowName()
    {
        var src = new VideoSourceConfig("cam", "uvc", "win:USB\\VID_1234", "USB3.0 Video", true);
        string? input = BuildFfmpegInput(src);
        Assert.Equal("-f dshow -i video=\"USB3.0 Video\"", input);
    }

    [Fact]
    /// <summary>
    /// Builds ffmpeg input uvc windows id uses dshow id.
    /// </summary>
    public void BuildFfmpegInput_UvcWindowsId_UsesDshowId()
    {
        var src = new VideoSourceConfig("cam", "uvc", "win:USB\\VID_1234", null, true);
        string? input = BuildFfmpegInput(src);
        Assert.Equal("-f dshow -i video=\"USB\\VID_1234\"", input);
    }

    [Fact]
    /// <summary>
    /// Builds ffmpeg input lavfi default uses test src 2.
    /// </summary>
    public void BuildFfmpegInput_LavfiDefault_UsesTestSrc2()
    {
        var src = new VideoSourceConfig("test", "lavfi", "", "Test", true);
        string? input = BuildFfmpegInput(src);
        Assert.Equal("-f lavfi -i testsrc2=size=1280x720:rate=30", input);
    }

    [Fact]
    /// <summary>
    /// Builds ffmpeg input lavfi custom uses provided spec.
    /// </summary>
    public void BuildFfmpegInput_LavfiCustom_UsesProvidedSpec()
    {
        var src = new VideoSourceConfig("test", "lavfi", "testsrc=size=640x480:rate=15", "Test", true);
        string? input = BuildFfmpegInput(src);
        Assert.Equal("-f lavfi -i testsrc=size=640x480:rate=15", input);
    }

    [Fact]
    /// <summary>
    /// Builds ffmpeg input rtsp uses tcp.
    /// </summary>
    public void BuildFfmpegInput_Rtsp_UsesTcp()
    {
        var src = new VideoSourceConfig("rtsp", "rtsp", "rtsp://127.0.0.1/stream", "RTSP", true);
        string? input = BuildFfmpegInput(src);
        Assert.Equal("-rtsp_transport tcp -i rtsp://127.0.0.1/stream", input);
    }

    [Fact]
    /// <summary>
    /// Builds ffmpeg input http uses direct input.
    /// </summary>
    public void BuildFfmpegInput_Http_UsesDirectInput()
    {
        var src = new VideoSourceConfig("http", "http", "http://127.0.0.1/stream.m3u8", "HLS", true);
        string? input = BuildFfmpegInput(src);
        Assert.Equal("-i http://127.0.0.1/stream.m3u8", input);
    }

    [Fact]
    /// <summary>
    /// Builds ffmpeg input screen returns platform specific input.
    /// </summary>
    public void BuildFfmpegInput_Screen_ReturnsPlatformSpecificInput()
    {
        var src = new VideoSourceConfig("screen", "screen", "", "Screen", true);
        string? input = BuildFfmpegInput(src);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("-f gdigrab -i desktop", input);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal("-f x11grab -i :0.0", input);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("-f avfoundation -i 1", input);
        }
        else
        {
            Assert.Null(input);
        }
    }
}
