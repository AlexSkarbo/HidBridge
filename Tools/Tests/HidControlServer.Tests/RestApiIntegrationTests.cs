using System.Net.Http.Json;
using System.Text.Json;
using HidControl.Application.Abstractions;
using HidControl.Application.UseCases;
using HidControl.Contracts;
using HidControl.Infrastructure.Services;
using HidControl.UseCases;
using HidControl.UseCases.Video;
using HidControlServer.Adapters.Application;
using HidControlServer.Endpoints.Sys;
using HidControlServer.Endpoints.Video;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HidControlServer.Tests;

public sealed class RestApiIntegrationTests
{
    private sealed class FakeVideoRuntime : IVideoRuntime
    {
        public bool IsManualStop(string id) => false;
        public void SetManualStop(string id, bool stopped) { }
        public Task<IReadOnlyList<string>> StopCaptureWorkersAsync(string? id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public bool StopFfmpegProcess(string id) => true;
        public IReadOnlyList<string> StopFfmpegProcesses() => Array.Empty<string>();
    }

    private sealed class FakeVideoOutputStateStore : IVideoOutputStateStore
    {
        private VideoOutputState _state = new(Hls: true, Mjpeg: true, Flv: false, MjpegPassthrough: false, MjpegFps: 30, MjpegSize: "");
        public VideoOutputState Get() => _state;
        public void Set(VideoOutputState state) => _state = state;
    }

    private sealed class FakeVideoFfmpegController : IVideoFfmpegController
    {
        public HidControl.UseCases.Video.FfmpegStartResult StartForSources(IReadOnlyList<VideoSourceConfig> sources, VideoOutputState outputState, bool restart, bool force)
            => new(Array.Empty<object>(), Array.Empty<string>());
    }

    private sealed class FakeVideoConfigStore : IVideoConfigStore
    {
        public HidControl.UseCases.Video.ConfigSaveResult SaveSources(IReadOnlyList<VideoSourceConfig> sources) => new(true, "test.json", null);
        public HidControl.UseCases.Video.ConfigSaveResult SaveActiveProfile(string activeProfile) => new(true, "test.json", null);
    }

    private sealed class FakeVideoConfigSaver : IVideoConfigSaver
    {
        public HidControl.Application.Models.ConfigSaveResult SaveSources(IReadOnlyList<VideoSourceConfig> sources) => new(true, "test.json", null);
        public HidControl.Application.Models.ConfigSaveResult SaveActiveProfile(string? activeProfile) => new(true, "test.json", null);
    }

    private sealed class FakeRoomIdService : IWebRtcRoomIdService
    {
        public string GenerateControlRoomId() => "hb-test-control";
        public string GenerateVideoRoomId() => "hb-v-test-video";
    }

    private sealed class FakeBackend : IWebRtcBackend
    {
        public string ControlRoom => "control";
        public string VideoRoom => "video";
        public string? GetDeviceIdHex() => null;
        public string StunUrl => "stun:stun.l.google.com:19302";
        public IReadOnlyList<string> TurnUrls => Array.Empty<string>();
        public string TurnSharedSecret => string.Empty;
        public int TurnTtlSeconds => 0;
        public string TurnUsername => string.Empty;
        public int ClientJoinTimeoutMs => 250;
        public int ClientConnectTimeoutMs => 5000;
        public int RoomsCleanupIntervalSeconds => 30;
        public int RoomIdleStopSeconds => 300;
        public int RoomsMaxHelpers => 8;
        public bool AutoStartPeersEnabled => false;
        public bool RoomsPersistenceEnabled => false;
        public string RoomsPersistencePath => string.Empty;
        public int RoomsPersistenceTtlSeconds => 0;
        public IReadOnlyDictionary<string, int> GetRoomPeerCountsSnapshot() => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlySet<string> GetHelperRoomsSnapshot() => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public (bool ok, bool started, int? pid, string? error) EnsureHelperStarted(string room, string? qualityPreset = null, int? bitrateKbps = null, int? fps = null, int? imageQuality = null, string? captureInput = null, string? encoder = null, string? codec = null, bool? audioEnabled = null, string? audioInput = null, int? audioBitrateKbps = null)
            => (true, true, 1234, null);
        public (bool ok, bool stopped, string? error) StopHelper(string room) => (true, true, null);
    }

    private static async Task<(WebApplication App, HttpClient Client)> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(new AppState());

        builder.Services.AddSingleton<IVideoRuntime, FakeVideoRuntime>();
        builder.Services.AddSingleton<VideoRuntimeService>();
        builder.Services.AddSingleton<IVideoOutputStateStore, FakeVideoOutputStateStore>();
        builder.Services.AddSingleton<VideoOutputService>();
        builder.Services.AddSingleton<IVideoFfmpegController, FakeVideoFfmpegController>();
        builder.Services.AddSingleton<VideoFfmpegService>();
        builder.Services.AddSingleton<IVideoConfigStore, FakeVideoConfigStore>();
        builder.Services.AddSingleton<VideoConfigService>();

        builder.Services.AddSingleton(new VideoSourceStore(Array.Empty<VideoSourceConfig>()));
        builder.Services.AddSingleton(new VideoProfileStore(Array.Empty<VideoProfileConfig>(), "low-latency"));
        builder.Services.AddSingleton<IVideoProfileStore, VideoProfileStoreAdapter>();
        builder.Services.AddSingleton<IVideoConfigSaver, FakeVideoConfigSaver>();

        builder.Services.AddSingleton<GetVideoProfilesUseCase>();
        builder.Services.AddSingleton<SetVideoProfilesUseCase>();
        builder.Services.AddSingleton<SetActiveVideoProfileUseCase>();
        builder.Services.AddSingleton<UpsertVideoProfileUseCase>();
        builder.Services.AddSingleton<DeleteVideoProfileUseCase>();

        builder.Services.AddSingleton<IWebRtcBackend, FakeBackend>();
        builder.Services.AddSingleton<IWebRtcRoomIdService, FakeRoomIdService>();
        builder.Services.AddSingleton<IWebRtcRoomsService, WebRtcRoomsService>();

        var app = builder.Build();
        app.MapVideoEndpoints();
        app.MapWebRtcStatusEndpoints();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task VideoProfileCrud_RestApi_Works()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, client) = await CreateAppAsync();
        await using var _ = app;

        var createResp = await client.PostAsJsonAsync("/video/profiles/upsert", new VideoProfileConfig("custom-rest", "-c:v libx264 -preset fast", "test"), ct);
        createResp.EnsureSuccessStatusCode();
        var createJson = await createResp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(createJson.GetProperty("ok").GetBoolean());

        var listResp = await client.GetAsync("/video/profiles", ct);
        listResp.EnsureSuccessStatusCode();
        var listJson = await listResp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(listJson.GetProperty("ok").GetBoolean());
        Assert.Contains(listJson.GetProperty("profiles").EnumerateArray(), p => string.Equals(p.GetProperty("name").GetString(), "custom-rest", StringComparison.OrdinalIgnoreCase));

        var delResp = await client.DeleteAsync("/video/profiles/custom-rest", ct);
        delResp.EnsureSuccessStatusCode();
        var delJson = await delResp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(delJson.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task VideoRoomProfileBinding_RestApi_Works()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, client) = await CreateAppAsync();
        await using var _ = app;

        var setResp = await client.PostAsJsonAsync("/status/webrtc/video/rooms/hb-v-room1/profile", new WebRtcCreateVideoRoomRequest(null, null, null, null, null, null, null, null, null, null, null, "low-bandwidth"), ct);
        setResp.EnsureSuccessStatusCode();
        var setJson = await setResp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(setJson.GetProperty("ok").GetBoolean());
        Assert.Equal("low-bandwidth", setJson.GetProperty("streamProfile").GetString());

        var getResp = await client.GetAsync("/status/webrtc/video/rooms/hb-v-room1/profile", ct);
        getResp.EnsureSuccessStatusCode();
        var getJson = await getResp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(getJson.GetProperty("ok").GetBoolean());
        Assert.Equal("low-bandwidth", getJson.GetProperty("streamProfile").GetString());
    }
}
