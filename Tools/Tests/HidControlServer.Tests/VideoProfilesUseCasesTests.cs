using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Application.UseCases;
using HidControl.Contracts;
using HidControl.UseCases;
using HidControlServer.Adapters.Application;
using Xunit;

namespace HidControlServer.Tests;

public sealed class VideoProfilesUseCasesTests
{
    private sealed class FakeVideoConfigSaver : IVideoConfigSaver
    {
        public string? LastActive { get; private set; }
        public ConfigSaveResult NextActiveSave { get; set; } = new(true, "cfg.json", null);

        public ConfigSaveResult SaveSources(IReadOnlyList<VideoSourceConfig> sources)
        {
            return new ConfigSaveResult(true, "cfg.json", null);
        }

        public ConfigSaveResult SaveActiveProfile(string? activeProfile)
        {
            LastActive = activeProfile;
            return NextActiveSave;
        }
    }

    private static VideoProfileStoreAdapter CreateStore(out VideoProfileStore rawStore)
    {
        rawStore = new VideoProfileStore(
            new[]
            {
                new VideoProfileConfig("balanced", "-c:v libx264 -preset veryfast", "default"),
                new VideoProfileConfig("quality", "-c:v libx264 -preset fast", "hq")
            },
            "balanced");
        return new VideoProfileStoreAdapter(rawStore);
    }

    [Fact]
    public void GetVideoProfilesUseCase_ReturnsSnapshot()
    {
        var store = CreateStore(out _);
        var uc = new GetVideoProfilesUseCase(store);

        var snap = uc.Execute();

        Assert.Equal("balanced", snap.ActiveProfile);
        Assert.Contains(snap.Profiles, p => p.Name == "quality");
    }

    [Fact]
    public void SetVideoProfilesUseCase_ReplacesAndActivates()
    {
        var store = CreateStore(out _);
        var uc = new SetVideoProfilesUseCase(store);
        var next = new[]
        {
            new VideoProfileConfig("low-latency", "-c:v libx264 -preset ultrafast", "ll"),
            new VideoProfileConfig("amf-low", "-c:v h264_amf", "amf")
        };

        var snap = uc.Execute(next, "amf-low");

        Assert.Equal("amf-low", snap.ActiveProfile);
        Assert.Contains(snap.Profiles, p => p.Name == "amf-low");
        Assert.Contains(snap.Profiles, p => p.Name == "low-latency" && p.IsReadonly);
    }

    [Fact]
    public void SetActiveVideoProfileUseCase_SavesConfig_WhenActivated()
    {
        var store = CreateStore(out _);
        var saver = new FakeVideoConfigSaver();
        var uc = new SetActiveVideoProfileUseCase(store, saver);

        var res = uc.Execute("quality");

        Assert.True(res.Ok);
        Assert.Equal("quality", res.Active);
        Assert.True(res.ConfigSaved);
        Assert.Equal("quality", saver.LastActive);
    }

    [Fact]
    public void SetActiveVideoProfileUseCase_DoesNotSave_WhenProfileMissing()
    {
        var store = CreateStore(out _);
        var saver = new FakeVideoConfigSaver();
        var uc = new SetActiveVideoProfileUseCase(store, saver);

        var res = uc.Execute("missing-profile");

        Assert.False(res.Ok);
        Assert.Equal("balanced", res.Active);
        Assert.Null(saver.LastActive);
    }

    [Fact]
    public void UpsertVideoProfileUseCase_RejectsReadonlyPreset()
    {
        var store = CreateStore(out _);
        var uc = new UpsertVideoProfileUseCase(store);

        var res = uc.Execute(new VideoProfileConfig("low-latency", "-c:v libx264", "x"));

        Assert.False(res.Ok);
        Assert.Equal("readonly_profile", res.Error);
    }

    [Fact]
    public void DeleteVideoProfileUseCase_RemovesUserProfile()
    {
        var store = CreateStore(out _);
        var upsert = new UpsertVideoProfileUseCase(store);
        var del = new DeleteVideoProfileUseCase(store);
        _ = upsert.Execute(new VideoProfileConfig("custom-z", "-c:v libx264 -preset fast", "z"));

        var res = del.Execute("custom-z");

        Assert.True(res.Ok);
        Assert.DoesNotContain(res.Profiles, p => string.Equals(p.Name, "custom-z", StringComparison.OrdinalIgnoreCase));
    }
}
