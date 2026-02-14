using HidControl.Contracts;
using HidControl.UseCases;
using Xunit;

namespace HidControlServer.Tests;

/// <summary>
/// Tests VideoProfileStoreTests.
/// </summary>
public sealed class VideoProfileStoreTests
{
    [Fact]
    /// <summary>
    /// Executes Constructor_UsesDefaults_WhenEmpty.
    /// </summary>
    public void Constructor_UsesDefaults_WhenEmpty()
    {
        var store = new VideoProfileStore(Array.Empty<VideoProfileConfig>(), "missing");
        var all = store.GetAll();

        Assert.True(all.Count >= 2);
        // Active profile is auto-selected based on OS preference.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("win-1080-low", store.ActiveProfile);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal("pi-1080-low", store.ActiveProfile);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("mac-1080-low", store.ActiveProfile);
        }
        else
        {
            Assert.Equal("low-latency", store.ActiveProfile);
        }

        Assert.Contains(all, p => string.Equals(p.Name, store.ActiveProfile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    /// <summary>
    /// Sets active returns false for missing profile.
    /// </summary>
    public void SetActive_ReturnsFalse_ForMissingProfile()
    {
        var store = new VideoProfileStore(new[]
        {
            new VideoProfileConfig("low-latency", "-c:v libx264", "default")
        }, "low-latency");

        bool ok = store.SetActive("missing");

        Assert.False(ok);
        Assert.Equal("low-latency", store.ActiveProfile);
    }

    [Fact]
    /// <summary>
    /// Executes ReplaceAll_ResetsActive_WhenMissing.
    /// </summary>
    public void ReplaceAll_ResetsActive_WhenMissing()
    {
        var store = new VideoProfileStore(new[]
        {
            new VideoProfileConfig("low-latency", "-c:v libx264", "default")
        }, "low-latency");

        store.ReplaceAll(new[]
        {
            new VideoProfileConfig("quality", "-c:v libx264 -crf 22", "default")
        });

        Assert.Equal("quality", store.ActiveProfile);
    }
}
