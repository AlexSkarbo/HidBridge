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

    [Fact]
    public void ReplaceAll_AlwaysKeepsReadonlyBasePresets()
    {
        var store = new VideoProfileStore(Array.Empty<VideoProfileConfig>(), "missing");

        store.ReplaceAll(new[]
        {
            new VideoProfileConfig("custom-1", "-c:v libx264 -preset veryfast", "custom")
        });

        var all = store.GetAll();
        Assert.Contains(all, p => p.Name == "custom-1" && !p.IsReadonly);
        Assert.Contains(all, p => p.Name == "pc-hdmi-pcm48" && p.IsReadonly);
        Assert.Contains(all, p => p.Name == "android-tvbox" && p.IsReadonly);
        Assert.Contains(all, p => p.Name == "low-bandwidth" && p.IsReadonly);
    }

    [Fact]
    public void Upsert_RejectsReadonlyBaseProfile()
    {
        var store = new VideoProfileStore(Array.Empty<VideoProfileConfig>(), "missing");

        bool ok = store.Upsert(new VideoProfileConfig("low-latency", "-c:v libx264", "x"), out string? error);

        Assert.False(ok);
        Assert.Equal("readonly_profile", error);
    }

    [Fact]
    public void Upsert_And_Delete_UserProfile()
    {
        var store = new VideoProfileStore(Array.Empty<VideoProfileConfig>(), "missing");

        bool created = store.Upsert(new VideoProfileConfig("custom-x", "-c:v libx264 -preset fast", "x"), out string? createError);
        bool deleted = store.Delete("custom-x", out string? deleteError);

        Assert.True(created);
        Assert.Null(createError);
        Assert.True(deleted);
        Assert.Null(deleteError);
        Assert.DoesNotContain(store.GetAll(), p => string.Equals(p.Name, "custom-x", StringComparison.OrdinalIgnoreCase));
    }
}
