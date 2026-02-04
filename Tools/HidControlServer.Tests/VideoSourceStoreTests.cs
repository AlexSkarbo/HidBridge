using HidControl.Contracts;
using HidControl.UseCases;
using Xunit;

namespace HidControlServer.Tests;

/// <summary>
/// Tests VideoSourceStoreTests.
/// </summary>
public sealed class VideoSourceStoreTests
{
    [Fact]
    /// <summary>
    /// Executes ReplaceAll_ReplacesItems.
    /// </summary>
    public void ReplaceAll_ReplacesItems()
    {
        var store = new VideoSourceStore(new[]
        {
            new VideoSourceConfig("a", "uvc", "/dev/video0", "A", true)
        });

        store.ReplaceAll(new[]
        {
            new VideoSourceConfig("b", "uvc", "/dev/video1", "B", false)
        });

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("b", all[0].Id);
    }

    [Fact]
    /// <summary>
    /// Executes Upsert_UpdatesExistingById.
    /// </summary>
    public void Upsert_UpdatesExistingById()
    {
        var store = new VideoSourceStore(new[]
        {
            new VideoSourceConfig("a", "uvc", "/dev/video0", "A", true)
        });

        store.Upsert(new VideoSourceConfig("a", "uvc", "/dev/video2", "A2", false));

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("/dev/video2", all[0].Url);
        Assert.False(all[0].Enabled);
    }
}
