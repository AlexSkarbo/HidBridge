using HidControl.Application.Abstractions;
using HidControl.Application.UseCases.WebRtc;
using HidControl.UseCases.Video;
using HidControl.Infrastructure.Services;
using HidControlServer;
using HidControlServer.Services;
using Xunit;

namespace HidControlServer.Tests;

public sealed class WebRtcIntegrationTests
{
    private sealed class FakeBackend : IWebRtcBackend
    {
        private readonly Dictionary<string, int> _peers;
        private readonly HashSet<string> _helperRooms;
        private readonly Queue<(bool ok, bool started, int? pid, string? error)> _ensureResults = new();
        private readonly Queue<(bool ok, bool stopped, string? error)> _stopResults = new();

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

        public bool RoomsPersistenceEnabled { get; set; }

        public string RoomsPersistencePath { get; set; } = Path.Combine(Path.GetTempPath(), $"webrtc_rooms_{Guid.NewGuid():N}.json");

        public int RoomsPersistenceTtlSeconds { get; set; } = 86_400;

        public int EnsureHelperStartedCalls { get; private set; }

        public int StopHelperCalls { get; private set; }
        public string? LastQualityPreset { get; private set; }

        public bool StopHelperOk { get; set; } = true;

        public bool StopHelperStopped { get; set; } = true;

        public string? StopHelperError { get; set; }
        public bool EnsureAddsHelperRoom { get; set; } = true;
        public bool EnsureAddsPeer { get; set; } = true;

        public void SetRoomPeers(string room, int peers) => _peers[room] = peers;
        public void EnqueueEnsureResult(bool ok, bool started, int? pid, string? error) => _ensureResults.Enqueue((ok, started, pid, error));
        public void EnqueueStopResult(bool ok, bool stopped, string? error) => _stopResults.Enqueue((ok, stopped, error));

        public void SetHelperRoom(string room, bool hasHelper)
        {
            if (hasHelper) _helperRooms.Add(room);
            else _helperRooms.Remove(room);
        }

        public IReadOnlyDictionary<string, int> GetRoomPeerCountsSnapshot() => new Dictionary<string, int>(_peers, StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> GetHelperRoomsSnapshot() => new HashSet<string>(_helperRooms, StringComparer.OrdinalIgnoreCase);

        public (bool ok, bool started, int? pid, string? error) EnsureHelperStarted(
            string room,
            string? qualityPreset = null,
            int? bitrateKbps = null,
            int? fps = null,
            int? imageQuality = null,
            string? captureInput = null,
            string? encoder = null,
            string? codec = null)
        {
            EnsureHelperStartedCalls++;
            LastQualityPreset = qualityPreset;
            var result = _ensureResults.Count > 0
                ? _ensureResults.Dequeue()
                : (true, true, 12345, null);

            if (result.ok && EnsureAddsHelperRoom)
            {
                _helperRooms.Add(room);
            }
            if (result.ok && EnsureAddsPeer)
            {
                // Simulate helper itself joining as a peer.
                _peers[room] = Math.Max(_peers.TryGetValue(room, out var c) ? c : 0, 1);
            }
            return result;
        }

        public (bool ok, bool stopped, string? error) StopHelper(string room)
        {
            StopHelperCalls++;
            _helperRooms.Remove(room);
            if (_stopResults.Count > 0)
            {
                return _stopResults.Dequeue();
            }
            return (StopHelperOk, StopHelperStopped, StopHelperError);
        }

        public string? GetDeviceIdHex() => DeviceIdHex;
    }

    private sealed class FakeVideoRuntime : IVideoRuntime
    {
        public bool IsManualStop(string id) => false;
        public void SetManualStop(string id, bool stopped) { }
        public Task<IReadOnlyList<string>> StopCaptureWorkersAsync(string? id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public bool StopFfmpegProcess(string id) => false;
        public IReadOnlyList<string> StopFfmpegProcesses() => Array.Empty<string>();
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

        var res = await uc.Execute(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.NotNull(res.Room);
        Assert.StartsWith("hb-v-50443405-", res.Room!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, backend.EnsureHelperStartedCalls);
    }

    [Fact]
    public async Task CreateVideoRoom_WithExplicitRoomId_PreservesRequestedRoom()
    {
        var backend = new FakeBackend(deviceIdHex: "50443405deadbeef");
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new CreateWebRtcVideoRoomUseCase(roomsSvc, roomIds);

        var res = await uc.Execute("hb-v-50443405-demo", null, null, null, null, null, null, null, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Equal("hb-v-50443405-demo", res.Room);
        Assert.True(res.Started);
        Assert.Equal(1, backend.EnsureHelperStartedCalls);
    }

    [Fact]
    public async Task CreateVideoRoom_WithQualityPreset_ForwardsPresetToBackend()
    {
        var backend = new FakeBackend(deviceIdHex: "50443405deadbeef");
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new CreateWebRtcVideoRoomUseCase(roomsSvc, roomIds);

        var res = await uc.Execute("hb-v-50443405-demo", "high", null, null, null, null, null, null, CancellationToken.None);

        Assert.True(res.Ok);
        Assert.Equal("high", backend.LastQualityPreset);
    }

    [Fact]
    public async Task CreateVideoRoom_WhenHelperStartTimeout_PropagatesTimeoutError()
    {
        var backend = new FakeBackend(deviceIdHex: "50443405deadbeef");
        backend.EnqueueEnsureResult(ok: false, started: false, pid: null, error: "timeout");
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new CreateWebRtcVideoRoomUseCase(roomsSvc, roomIds);

        var res = await uc.Execute("hb-v-50443405-timeout", "high", 1500, 30, null, null, "cpu", "h264", CancellationToken.None);

        Assert.False(res.Ok);
        Assert.False(res.Started);
        Assert.Equal("timeout", res.Error);
        Assert.Equal(1, backend.EnsureHelperStartedCalls);
    }

    [Fact]
    public async Task ListVideoRooms_WhenHelperFlagLagsBehindRuntime_PreservesRoomFromPeersSnapshot()
    {
        var backend = new FakeBackend();
        backend.SetRoomPeers("hb-v-50443405-lag", 1);
        backend.SetHelperRoom("hb-v-50443405-lag", false);
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new ListWebRtcRoomsUseCase(roomsSvc);

        var snap = await uc.Execute(CancellationToken.None);

        var room = Assert.Single(snap.Rooms, r => string.Equals(r.Room, "hb-v-50443405-lag", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, room.Peers);
        Assert.False(room.HasHelper);
    }

    [Fact]
    public async Task DeleteVideoRoom_WhenStopFailsButRoomAlreadyGone_ReconcilesAsSuccess()
    {
        var backend = new FakeBackend(deviceIdHex: "50443405deadbeef");
        backend.EnqueueStopResult(ok: false, stopped: false, error: "timeout");
        var roomIds = new WebRtcRoomIdService(backend);
        var roomsSvc = new WebRtcRoomsService(backend, roomIds);
        var uc = new DeleteWebRtcVideoRoomUseCase(roomsSvc);

        // Room is already effectively gone from runtime snapshots.
        backend.SetHelperRoom("hb-v-50443405-gone", false);
        backend.SetRoomPeers("hb-v-50443405-gone", 0);

        var res = await uc.Execute("hb-v-50443405-gone", CancellationToken.None);

        Assert.True(res.Ok);
        // Stop flag may be true/false depending on backend reconciliation details;
        // success + no error is the contract we care about here.
        Assert.Null(res.Error);
        Assert.True(backend.StopHelperCalls >= 1);
    }

    [Fact]
    public void VideoRuntimeStatus_WhenFallbackReported_StoresFallbackAndLastError()
    {
        var opt = Options.Parse(Array.Empty<string>());
        var signaling = new WebRtcSignalingService();
        var runtime = new FakeVideoRuntime();
        using var supervisor = new WebRtcVideoPeerSupervisor(opt, signaling, runtime);
        const string room = "hb-v-50443405-fallback";

        supervisor.ReportRuntimeStatus(
            room,
            eventName: "pipeline_started",
            sourceModeActive: "capture",
            detail: null,
            encoder: "cpu",
            codec: "h264");

        supervisor.ReportRuntimeStatus(
            room,
            eventName: "fallback",
            sourceModeActive: "testsrc",
            detail: "capture_failed",
            fallbackUsed: true);

        var status = supervisor.GetRoomRuntimeStatus(room);

        Assert.NotNull(status);
        Assert.True(status!.FallbackUsed);
        Assert.Equal("capture_failed", status.LastVideoError);
        Assert.Equal("testsrc", status.SourceModeActive);
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
    public async Task RoomsPersistence_RestoresRoomAndStartsHelper_OnNewServiceInstance()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hidbridge-rtc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string storePath = Path.Combine(tempDir, "webrtc_rooms.json");

        try
        {
            var backendA = new FakeBackend(deviceIdHex: "50443405deadbeef")
            {
                RoomsPersistenceEnabled = true,
                RoomsPersistencePath = storePath,
                RoomsPersistenceTtlSeconds = 3600
            };
            var roomIdsA = new WebRtcRoomIdService(backendA);
            var roomsSvcA = new WebRtcRoomsService(backendA, roomIdsA);

            var createUc = new CreateWebRtcRoomUseCase(roomsSvcA);
            var created = await createUc.Execute("hb-50443405-persist", CancellationToken.None);
            Assert.True(created.Ok);
            Assert.True(File.Exists(storePath));

            var backendB = new FakeBackend(deviceIdHex: "50443405deadbeef")
            {
                RoomsPersistenceEnabled = true,
                RoomsPersistencePath = storePath,
                RoomsPersistenceTtlSeconds = 3600
            };
            var roomIdsB = new WebRtcRoomIdService(backendB);
            var roomsSvcB = new WebRtcRoomsService(backendB, roomIdsB);
            var listUc = new ListWebRtcRoomsUseCase(roomsSvcB);

            var snap = await listUc.Execute(CancellationToken.None);

            Assert.Equal(1, backendB.EnsureHelperStartedCalls);
            Assert.Contains(snap.Rooms, r => string.Equals(r.Room, "hb-50443405-persist", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RoomsPersistence_TtlSkipsExpiredRooms()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hidbridge-rtc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string storePath = Path.Combine(tempDir, "webrtc_rooms.json");

        try
        {
            var old = DateTimeOffset.UtcNow.AddHours(-2);
            var json = $$"""
            {
              "version": 1,
              "rooms": [
                {
                  "room": "hb-50443405-old",
                  "createdAtUtc": "{{old:O}}",
                  "updatedAtUtc": "{{old:O}}"
                }
              ]
            }
            """;
            File.WriteAllText(storePath, json);

            var backend = new FakeBackend(deviceIdHex: "50443405deadbeef")
            {
                RoomsPersistenceEnabled = true,
                RoomsPersistencePath = storePath,
                RoomsPersistenceTtlSeconds = 60
            };
            var roomIds = new WebRtcRoomIdService(backend);
            var roomsSvc = new WebRtcRoomsService(backend, roomIds);
            var listUc = new ListWebRtcRoomsUseCase(roomsSvc);

            var snap = await listUc.Execute(CancellationToken.None);

            Assert.DoesNotContain(snap.Rooms, r => string.Equals(r.Room, "hb-50443405-old", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, backend.EnsureHelperStartedCalls);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeleteRoom_RemovesPersistedEntry_EvenWhenStopFails()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hidbridge-rtc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string storePath = Path.Combine(tempDir, "webrtc_rooms.json");

        try
        {
            var backendA = new FakeBackend(deviceIdHex: "50443405deadbeef")
            {
                RoomsPersistenceEnabled = true,
                RoomsPersistencePath = storePath,
                RoomsPersistenceTtlSeconds = 3600
            };
            var roomIdsA = new WebRtcRoomIdService(backendA);
            var roomsSvcA = new WebRtcRoomsService(backendA, roomIdsA);
            var createUc = new CreateWebRtcRoomUseCase(roomsSvcA);
            var created = await createUc.Execute("hb-50443405-stale", CancellationToken.None);
            Assert.True(created.Ok);

            backendA.StopHelperOk = false;
            backendA.StopHelperStopped = false;
            backendA.StopHelperError = "stop_failed";

            var deleteUc = new DeleteWebRtcRoomUseCase(roomsSvcA);
            var deleted = await deleteUc.Execute("hb-50443405-stale", CancellationToken.None);
            Assert.False(deleted.Ok);
            Assert.Equal("stop_failed", deleted.Error);

            var backendB = new FakeBackend(deviceIdHex: "50443405deadbeef")
            {
                RoomsPersistenceEnabled = true,
                RoomsPersistencePath = storePath,
                RoomsPersistenceTtlSeconds = 3600
            };
            var roomIdsB = new WebRtcRoomIdService(backendB);
            var roomsSvcB = new WebRtcRoomsService(backendB, roomIdsB);
            var listUc = new ListWebRtcRoomsUseCase(roomsSvcB);
            var snap = await listUc.Execute(CancellationToken.None);

            Assert.DoesNotContain(snap.Rooms, r => string.Equals(r.Room, "hb-50443405-stale", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
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

        var created = await createVideo.Execute(null, null, null, null, null, null, null, null, CancellationToken.None);
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
