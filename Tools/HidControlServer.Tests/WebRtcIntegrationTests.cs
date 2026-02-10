using HidControl.Application.Abstractions;
using HidControl.Application.UseCases.WebRtc;
using HidControl.Infrastructure.Services;
using Xunit;

namespace HidControlServer.Tests;

public sealed class WebRtcIntegrationTests
{
    private sealed class FakeBackend : IWebRtcBackend
    {
        private readonly Dictionary<string, int> _peers;
        private readonly HashSet<string> _helperRooms;

        public FakeBackend(string? deviceIdHex = "a1b2c3d4e5f6")
        {
            DeviceIdHex = deviceIdHex;
            _peers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _helperRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public string? DeviceIdHex { get; set; }

        public string StunUrl { get; set; } = "stun:stun.l.google.com:19302";

        public IReadOnlyList<string> TurnUrls { get; set; } = Array.Empty<string>();

        public string TurnSharedSecret { get; set; } = string.Empty;

        public int TurnTtlSeconds { get; set; } = 3600;

        public string TurnUsername { get; set; } = "hidbridge";

        public int ClientJoinTimeoutMs { get; set; } = 2000;

        public int ClientConnectTimeoutMs { get; set; } = 8000;

        public int RoomsCleanupIntervalSeconds { get; set; } = 2;

        public int RoomIdleStopSeconds { get; set; } = 60;

        public int RoomsMaxHelpers { get; set; } = 8;

        public int EnsureHelperStartedCalls { get; private set; }

        public int StopHelperCalls { get; private set; }

        public void SetRoomPeers(string room, int peers) => _peers[room] = peers;

        public void SetHelperRoom(string room, bool hasHelper)
        {
            if (hasHelper) _helperRooms.Add(room);
            else _helperRooms.Remove(room);
        }

        public IReadOnlyDictionary<string, int> GetRoomPeerCountsSnapshot() => new Dictionary<string, int>(_peers, StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> GetHelperRoomsSnapshot() => new HashSet<string>(_helperRooms, StringComparer.OrdinalIgnoreCase);

        public (bool ok, bool started, int? pid, string? error) EnsureHelperStarted(string room)
        {
            EnsureHelperStartedCalls++;
            _helperRooms.Add(room);
            // Simulate helper itself joining as a peer.
            _peers[room] = Math.Max(_peers.TryGetValue(room, out var c) ? c : 0, 1);
            return (true, true, 12345, null);
        }

        public (bool ok, bool stopped, string? error) StopHelper(string room)
        {
            StopHelperCalls++;
            _helperRooms.Remove(room);
            return (true, true, null);
        }

        public string? GetDeviceIdHex() => DeviceIdHex;
    }

    [Fact]
    public async Task ListRooms_IncludesPeerAndHelperRooms_AndMarksControl()
    {
        var backend = new FakeBackend();
        backend.SetRoomPeers("control", 2);
        backend.SetHelperRoom("control", true);
        backend.SetRoomPeers("hb-test", 0);
        backend.SetHelperRoom("hb-helper", true);

        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new ListWebRtcRoomsUseCase(roomsSvc);

        var snap = await uc.Execute(CancellationToken.None);

        Assert.Contains(snap.Rooms, r => r.Room.Equals("control", StringComparison.OrdinalIgnoreCase) && r.IsControl && r.HasHelper && r.Peers == 2);
        Assert.Contains(snap.Rooms, r => r.Room.Equals("hb-test", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snap.Rooms, r => r.Room.Equals("hb-helper", StringComparison.OrdinalIgnoreCase) && r.HasHelper);
    }

    [Fact]
    public async Task CreateRoom_GeneratesRoomId_AndStartsHelper()
    {
        var backend = new FakeBackend(deviceIdHex: "50443405deadbeef");
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new CreateWebRtcRoomUseCase(roomsSvc);

        var res = await uc.Execute(null, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.True(res.Started);
        Assert.NotNull(res.Room);
        Assert.StartsWith("hb-50443405-", res.Room!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, backend.EnsureHelperStartedCalls);
    }

    [Fact]
    public async Task CreateRoom_GeneratesFallbackRoomId_WhenDeviceIdMissing()
    {
        var backend = new FakeBackend(deviceIdHex: null);
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new CreateWebRtcRoomUseCase(roomsSvc);

        var res = await uc.Execute(null, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.NotNull(res.Room);
        Assert.StartsWith("hb-unknown-", res.Room!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRoom_WithExplicitRoomId_StartsHelperForThatRoom()
    {
        var backend = new FakeBackend(deviceIdHex: "50443405deadbeef");
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new CreateWebRtcRoomUseCase(roomsSvc);

        var res = await uc.Execute("hb-50443405-demo", CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Equal("hb-50443405-demo", res.Room);
        Assert.True(res.Started);
        Assert.Equal(1, backend.EnsureHelperStartedCalls);
    }

    [Fact]
    public async Task CreateRoom_RejectsInvalidRoomId()
    {
        var backend = new FakeBackend();
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new CreateWebRtcRoomUseCase(roomsSvc);

        var res = await uc.Execute("bad room with spaces", CancellationToken.None);

        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Equal(0, backend.EnsureHelperStartedCalls);
    }

    [Fact]
    public async Task DeleteRoom_RejectsControlRoom()
    {
        var backend = new FakeBackend();
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new DeleteWebRtcRoomUseCase(roomsSvc);

        var res = await uc.Execute("control", CancellationToken.None);

        Assert.False(res.Ok);
        Assert.Equal("cannot_delete_control", res.Error);
        Assert.Equal(0, backend.StopHelperCalls);
    }

    [Fact]
    public async Task DeleteRoom_RejectsDefaultVideoRoom()
    {
        var backend = new FakeBackend();
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new DeleteWebRtcRoomUseCase(roomsSvc);

        var res = await uc.Execute("video", CancellationToken.None);

        Assert.False(res.Ok);
        Assert.Equal("cannot_delete_video", res.Error);
        Assert.Equal(0, backend.StopHelperCalls);
    }

    [Fact]
    public async Task IceConfig_WithTurnSecret_ReturnsTurnWithPasswordCredentialType()
    {
        var backend = new FakeBackend();
        backend.StunUrl = "stun:stun.l.google.com:19302";
        backend.TurnUrls = new[] { "turn:127.0.0.1:3478?transport=udp", "turn:127.0.0.1:3478?transport=tcp" };
        backend.TurnSharedSecret = "secret";
        backend.TurnTtlSeconds = 3600;
        backend.TurnUsername = "hidbridge";

        var iceSvc = new WebRtcIceService(backend);
        var uc = new GetWebRtcIceConfigUseCase(iceSvc);

        var cfg = await uc.Execute(CancellationToken.None);

        Assert.NotNull(cfg.TtlSeconds);
        Assert.True(cfg.IceServers.Count >= 2);
        Assert.Contains(cfg.IceServers, s => s.Urls.Any(u => u.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(cfg.IceServers, s =>
            s.Urls.Any(u => u.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(s.Username) &&
            !string.IsNullOrWhiteSpace(s.Credential) &&
            string.Equals(s.CredentialType, "password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateVideoRoom_GeneratesRoomIdWithVideoPrefix_AndStartsHelper()
    {
        var backend = new FakeBackend(deviceIdHex: "50443405deadbeef");
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new CreateWebRtcVideoRoomUseCase(roomsSvc, roomIds);

        var res = await uc.Execute(null, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.NotNull(res.Room);
        Assert.StartsWith("hb-v-50443405-", res.Room!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, backend.EnsureHelperStartedCalls);
    }

    [Fact]
    public void JoinSignalingUseCase_JoinsControlRoom_AndClassifiesKind()
    {
        var signaling = new WebRtcSignalingService();
        var uc = new JoinWebRtcSignalingUseCase(signaling);

        var res = uc.Execute(null, "control", "client-a");

        Assert.True(res.Ok);
        Assert.Equal("control", res.Room);
        Assert.Equal(WebRtcRoomKind.Control, res.Kind);
        Assert.Equal(1, res.Peers);
    }

    [Fact]
    public void JoinSignalingUseCase_JoinsVideoRoom_AndClassifiesKind()
    {
        var signaling = new WebRtcSignalingService();
        var uc = new JoinWebRtcSignalingUseCase(signaling);

        var res = uc.Execute(null, "hb-v-a1b2c3d4-demo", "client-v");

        Assert.True(res.Ok);
        Assert.Equal("hb-v-a1b2c3d4-demo", res.Room);
        Assert.Equal(WebRtcRoomKind.Video, res.Kind);
        Assert.Equal(1, res.Peers);
    }

    [Fact]
    public void ValidateSignalUseCase_DeniesWhenNotJoined()
    {
        var signaling = new WebRtcSignalingService();
        var uc = new ValidateWebRtcSignalUseCase(signaling);

        var res = uc.Execute("control", "client-a");

        Assert.False(res.Ok);
        Assert.Equal("not_joined", res.Error);
    }

    [Fact]
    public void LeaveSignalingUseCase_ReturnsPeersAfterLeave()
    {
        var signaling = new WebRtcSignalingService();
        Assert.True(signaling.TryJoin("video", "helper").Ok);
        Assert.True(signaling.TryJoin("video", "client-a").Ok);

        var uc = new LeaveWebRtcSignalingUseCase(signaling);
        var res = uc.Execute("video", "client-a");

        Assert.Equal("video", res.Room);
        Assert.Equal(WebRtcRoomKind.Video, res.Kind);
        Assert.Equal(1, res.PeersLeft);
    }

    [Fact]
    public async Task DeleteVideoRoom_RejectsDefaultVideoRoom()
    {
        var backend = new FakeBackend();
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new DeleteWebRtcVideoRoomUseCase(roomsSvc);

        var res = await uc.Execute("video", CancellationToken.None);

        Assert.False(res.Ok);
        Assert.Equal("cannot_delete_video", res.Error);
    }

    [Fact]
    public async Task ClientConfig_UsesBackendTimeouts()
    {
        var backend = new FakeBackend
        {
            ClientJoinTimeoutMs = 111,
            ClientConnectTimeoutMs = 222,
            RoomsCleanupIntervalSeconds = 3,
            RoomIdleStopSeconds = 4,
            RoomsMaxHelpers = 5
        };
        var cfgSvc = new WebRtcConfigService(backend);
        var uc = new GetWebRtcClientConfigUseCase(cfgSvc);

        var cfg = await uc.Execute(CancellationToken.None);

        // The config service clamps very small timeouts to sane minimums.
        Assert.Equal(250, cfg.JoinTimeoutMs);
        Assert.Equal(250, cfg.ConnectTimeoutMs);
        Assert.Equal(3, cfg.RoomsCleanupIntervalSeconds);
        Assert.Equal(4, cfg.RoomIdleStopSeconds);
        Assert.Equal(5, cfg.RoomsMaxHelpers);
    }

    [Fact]
    public async Task VideoSignalingMvp_Smoke_CreateJoinSignalLeave()
    {
        var backend = new FakeBackend(deviceIdHex: "50443405deadbeef");
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var signaling = new WebRtcSignalingService();

        var createVideo = new CreateWebRtcVideoRoomUseCase(roomsSvc, roomIds);
        var join = new JoinWebRtcSignalingUseCase(signaling);
        var validateSignal = new ValidateWebRtcSignalUseCase(signaling);
        var leave = new LeaveWebRtcSignalingUseCase(signaling);

        var created = await createVideo.Execute(null, CancellationToken.None);
        Assert.True(created.Ok);
        Assert.NotNull(created.Room);
        Assert.StartsWith("hb-v-50443405-", created.Room!, StringComparison.OrdinalIgnoreCase);

        var joined = join.Execute(null, created.Room, "video-client");
        Assert.True(joined.Ok);
        Assert.Equal(WebRtcRoomKind.Video, joined.Kind);

        var canSignal = validateSignal.Execute(created.Room, "video-client");
        Assert.True(canSignal.Ok);

        var left = leave.Execute(created.Room!, "video-client");
        Assert.Equal(WebRtcRoomKind.Video, left.Kind);
        Assert.Equal(0, left.PeersLeft);
    }
}
