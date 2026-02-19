using HidControl.Application.Abstractions;
using HidControl.Contracts;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Default implementation of WebRTC room lifecycle service.
/// </summary>
public sealed class WebRtcRoomsService : IWebRtcRoomsService
{
    private const int PersistFileVersion = 1;
    private const int PersistActivityWriteMinSeconds = 30;
    private static readonly TimeSpan VideoOpLockProbeInterval = TimeSpan.FromSeconds(5);

    private readonly IWebRtcBackend _backend;
    private readonly IWebRtcRoomIdService _roomIds;
    private readonly IVideoProfileStore _videoProfiles;
    private readonly ILogger<WebRtcRoomsService> _logger;
    private readonly object _videoProfilesLock = new();
    private readonly Dictionary<string, string?> _videoRoomProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _videoRoomOpLock = new(1, 1);
    private readonly object _persistLock = new();
    private readonly Dictionary<string, PersistedRoomState> _persistedRooms = new(StringComparer.OrdinalIgnoreCase);
    private bool _restoreAttempted;
    private readonly object _opDiagLock = new();
    private string? _videoOpOwner;
    private DateTimeOffset? _videoOpOwnerSinceUtc;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="backend">Backend.</param>
    public WebRtcRoomsService(
        IWebRtcBackend backend,
        IWebRtcRoomIdService roomIds,
        IVideoProfileStore? videoProfiles = null,
        ILogger<WebRtcRoomsService>? logger = null)
    {
        _backend = backend;
        _roomIds = roomIds;
        _videoProfiles = videoProfiles ?? EmptyVideoProfileStore.Instance;
        _logger = logger ?? NullLogger<WebRtcRoomsService>.Instance;
        LoadPersistedRooms();
    }

    private sealed class EmptyVideoProfileStore : IVideoProfileStore
    {
        public static readonly EmptyVideoProfileStore Instance = new();
        public string ActiveProfile => string.Empty;
        public IReadOnlyList<VideoProfileConfig> GetAll() => Array.Empty<VideoProfileConfig>();
        public void ReplaceAll(IReadOnlyList<VideoProfileConfig> profiles) { }
        public bool SetActive(string name) => false;
        public bool Upsert(VideoProfileConfig profile, out string? error)
        {
            error = "store_unavailable";
            return false;
        }

        public bool Delete(string name, out string? error)
        {
            error = "store_unavailable";
            return false;
        }
    }

    /// <inheritdoc />
    public Task<WebRtcRoomsSnapshot> ListAsync(CancellationToken ct)
    {
        _ = ct;
        RestorePersistedRoomsIfNeeded();

        var peers = _backend.GetRoomPeerCountsSnapshot();
        var helperRooms = _backend.GetHelperRoomsSnapshot();
        RefreshPersistenceFromActivity(peers, helperRooms);
        CleanupPersistedRooms(peers, helperRooms);

        var allRooms = new HashSet<string>(peers.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var hr in helperRooms) allRooms.Add(hr);
        foreach (var pr in GetPersistedRoomIdsSnapshot()) allRooms.Add(pr);
        foreach (var vr in GetKnownVideoRoomsSnapshot()) allRooms.Add(vr);

        var rooms = allRooms
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .Select(r => new WebRtcRoomInfo(
                r,
                peers.TryGetValue(r, out int c) ? c : 0,
                helperRooms.Contains(r),
                string.Equals(r, "control", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return Task.FromResult(new WebRtcRoomsSnapshot(rooms));
    }

    /// <inheritdoc />
    public Task<WebRtcRoomCreateResult> CreateAsync(string? room, CancellationToken ct)
    {
        return CreateInternalAsync(room, qualityPreset: null, bitrateKbps: null, fps: null, imageQuality: null, captureInput: null, encoder: null, codec: null, audioEnabled: null, audioInput: null, audioBitrateKbps: null, streamProfile: null, persist: true, ct);
    }

    /// <inheritdoc />
    public async Task<WebRtcRoomCreateResult> CreateVideoAsync(string? room, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec, bool? audioEnabled, string? audioInput, int? audioBitrateKbps, string? streamProfile, CancellationToken ct)
    {
        string opId = Guid.NewGuid().ToString("N")[..8];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("create_video begin op={OpId} room={Room} profile={Profile}", opId, room, streamProfile);
        bool lockHeld = false;
        if (!await TryAcquireVideoOpLockAsync($"create:{opId}", ct))
        {
            var owner = GetVideoOpOwner();
            _logger.LogWarning("create_video busy op={OpId} room={Room} owner={Owner} elapsedMs={ElapsedMs}", opId, room, owner, sw.ElapsedMilliseconds);
            return new WebRtcRoomCreateResult(false, room, false, null, "busy");
        }
        lockHeld = true;
        try
        {
            // Persist video rooms as first-class entities (even when helper is later rotated/stopped),
            // so room list and explicit delete stay stable from UI perspective.
            var res = await CreateInternalAsync(room, qualityPreset, bitrateKbps, fps, imageQuality, captureInput, encoder, codec, audioEnabled, audioInput, audioBitrateKbps, streamProfile, persist: true, ct);
            _logger.LogInformation("create_video end op={OpId} room={Room} ok={Ok} started={Started} err={Err} elapsedMs={ElapsedMs}", opId, res.Room, res.Ok, res.Started, res.Error, sw.ElapsedMilliseconds);
            return res;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("create_video canceled op={OpId} room={Room} elapsedMs={ElapsedMs}", opId, room, sw.ElapsedMilliseconds);
            return new WebRtcRoomCreateResult(false, room, false, null, "canceled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "create_video error op={OpId} room={Room} elapsedMs={ElapsedMs}", opId, room, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            if (lockHeld) ReleaseVideoOpLock($"create:{opId}");
        }
    }

    /// <inheritdoc />
    public async Task<WebRtcRoomDeleteResult> DeleteAsync(string room, CancellationToken ct)
    {
        string opId = Guid.NewGuid().ToString("N")[..8];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("delete begin op={OpId} room={Room}", opId, room);
        bool lockHeld = false;
        if (!await TryAcquireVideoOpLockAsync($"delete:{opId}", ct))
        {
            var owner = GetVideoOpOwner();
            _logger.LogWarning("delete busy op={OpId} room={Room} owner={Owner} elapsedMs={ElapsedMs}", opId, room, owner, sw.ElapsedMilliseconds);
            return new WebRtcRoomDeleteResult(false, room, false, "busy");
        }
        lockHeld = true;
        try
        {
        if (string.Equals(room, "control", StringComparison.OrdinalIgnoreCase))
        {
            return new WebRtcRoomDeleteResult(false, room, false, "cannot_delete_control");
        }
        if (string.Equals(room, "video", StringComparison.OrdinalIgnoreCase))
        {
            return new WebRtcRoomDeleteResult(false, room, false, "cannot_delete_video");
        }

        (bool ok, bool stopped, string? error) stop = (false, false, "delete_failed");
        for (int i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            stop = _backend.StopHelper(room);
            if (stop.ok)
            {
                break;
            }

            await Task.Delay(150 * (i + 1), ct);
        }

        // Explicit delete should always remove persisted room metadata,
        // even if backend stop returns a transient/process-race error.
        RemovePersistedRoom(room);
        lock (_videoProfilesLock)
        {
            _videoRoomProfiles.Remove(room);
        }

        if (!stop.ok)
        {
            // Reconcile races: if helper is no longer known and no peers remain,
            // treat delete as successful even if StopHelper reported a transient error.
            var peers = _backend.GetRoomPeerCountsSnapshot();
            var helperRooms = _backend.GetHelperRoomsSnapshot();
            bool hasHelper = helperRooms.Contains(room);
            bool hasPeers = peers.TryGetValue(room, out int peerCount) && peerCount > 0;
            if (!hasHelper && !hasPeers)
            {
                return new WebRtcRoomDeleteResult(true, room, stop.stopped, null);
            }
        }

        return new WebRtcRoomDeleteResult(stop.ok, room, stop.stopped, stop.error);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("delete canceled op={OpId} room={Room} elapsedMs={ElapsedMs}", opId, room, sw.ElapsedMilliseconds);
            return new WebRtcRoomDeleteResult(false, room, false, "canceled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "delete error op={OpId} room={Room} elapsedMs={ElapsedMs}", opId, room, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            if (lockHeld) ReleaseVideoOpLock($"delete:{opId}");
            _logger.LogInformation("delete end op={OpId} room={Room} elapsedMs={ElapsedMs}", opId, room, sw.ElapsedMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<WebRtcRoomRestartResult> RestartVideoAsync(string room, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec, bool? audioEnabled, string? audioInput, int? audioBitrateKbps, string? streamProfile, CancellationToken ct)
    {
        string opId = Guid.NewGuid().ToString("N")[..8];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("restart_video begin op={OpId} room={Room} profile={Profile}", opId, room, streamProfile);
        bool lockHeld = false;
        if (!await TryAcquireVideoOpLockAsync($"restart:{opId}", ct))
        {
            var owner = GetVideoOpOwner();
            _logger.LogWarning("restart_video busy op={OpId} room={Room} owner={Owner} elapsedMs={ElapsedMs}", opId, room, owner, sw.ElapsedMilliseconds);
            return new WebRtcRoomRestartResult(false, room, false, false, null, "busy");
        }
        lockHeld = true;
        try
        {
        if (string.IsNullOrWhiteSpace(room))
        {
            return new WebRtcRoomRestartResult(false, room ?? string.Empty, false, false, null, "room_required");
        }

        string normalizedRoom = room.Trim();
        if (!IsVideoRoom(normalizedRoom))
        {
            return new WebRtcRoomRestartResult(false, normalizedRoom, false, false, null, "video_room_required");
        }

        // Keep the same guardrails as create endpoint for user-provided params.
        if (!string.IsNullOrWhiteSpace(qualityPreset))
        {
            string q = qualityPreset.Trim().ToLowerInvariant();
            if (q != "low" && q != "low-latency" && q != "balanced" && q != "high" && q != "optimal")
            {
                return new WebRtcRoomRestartResult(false, normalizedRoom, false, false, null, "bad_quality_preset");
            }
        }
        if (bitrateKbps is int b && (b < 200 || b > 12000))
        {
            return new WebRtcRoomRestartResult(false, normalizedRoom, false, false, null, "bad_bitrate_kbps");
        }
        if (fps is int f && (f < 5 || f > 60))
        {
            return new WebRtcRoomRestartResult(false, normalizedRoom, false, false, null, "bad_fps");
        }
        if (imageQuality is int iq && (iq < 1 || iq > 100))
        {
            return new WebRtcRoomRestartResult(false, normalizedRoom, false, false, null, "bad_image_quality");
        }
        if (audioBitrateKbps is int ab && (ab < 16 || ab > 512))
        {
            return new WebRtcRoomRestartResult(false, normalizedRoom, false, false, null, "bad_audio_bitrate_kbps");
        }

        string? explicitStreamProfile = NormalizeRoomStreamProfileName(streamProfile);
        if (!string.IsNullOrWhiteSpace(explicitStreamProfile) && ResolveExistingStreamProfileName(explicitStreamProfile) is null)
        {
            return new WebRtcRoomRestartResult(false, normalizedRoom, false, false, null, "profile_not_found");
        }

        string? effectiveStreamProfile = ResolveExistingStreamProfileName(ResolveRoomStreamProfileForStart(normalizedRoom, streamProfile));

        ResolveEffectiveVideoSettings(
            effectiveStreamProfile,
            qualityPreset,
            bitrateKbps,
            fps,
            imageQuality,
            captureInput,
            encoder,
            codec,
            audioEnabled,
            audioInput,
            audioBitrateKbps,
            out string? normalizedPreset,
            out int? normalizedBitrate,
            out int? normalizedFps,
            out int? normalizedImageQuality,
            out string? normalizedCaptureInput,
            out string? normalizedEncoder,
            out string? normalizedCodec,
            out bool? normalizedAudioEnabled,
            out string? normalizedAudioInput,
            out int? normalizedAudioBitrate);

        (bool ok, bool stopped, string? error) stop = (false, false, "restart_stop_failed");
        for (int i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            stop = _backend.StopHelper(normalizedRoom);
            if (stop.ok)
            {
                break;
            }
            await Task.Delay(120 * (i + 1), ct);
        }

        if (!stop.ok)
        {
            // Same reconciliation rule as delete: if room is already gone, continue restart.
            var peers = _backend.GetRoomPeerCountsSnapshot();
            var helpers = _backend.GetHelperRoomsSnapshot();
            bool hasHelper = helpers.Contains(normalizedRoom);
            bool hasPeers = peers.TryGetValue(normalizedRoom, out int peerCount) && peerCount > 0;
            if (hasHelper || hasPeers)
            {
                return new WebRtcRoomRestartResult(false, normalizedRoom, stop.stopped, false, null, stop.error, effectiveStreamProfile);
            }
        }

        var start = _backend.EnsureHelperStarted(normalizedRoom, normalizedPreset, normalizedBitrate, normalizedFps, normalizedImageQuality, normalizedCaptureInput, normalizedEncoder, normalizedCodec, normalizedAudioEnabled, normalizedAudioInput, normalizedAudioBitrate);
        if (!start.ok)
        {
            return new WebRtcRoomRestartResult(false, normalizedRoom, stop.stopped, start.started, start.pid, start.error, effectiveStreamProfile);
        }

        RememberRoomStreamProfile(normalizedRoom, effectiveStreamProfile);

        var result = new WebRtcRoomRestartResult(true, normalizedRoom, stop.stopped, start.started, start.pid, null, effectiveStreamProfile);
        _logger.LogInformation("restart_video end op={OpId} room={Room} ok={Ok} started={Started} err={Err} elapsedMs={ElapsedMs}", opId, result.Room, result.Ok, result.Started, result.Error, sw.ElapsedMilliseconds);
        return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("restart_video canceled op={OpId} room={Room} elapsedMs={ElapsedMs}", opId, room, sw.ElapsedMilliseconds);
            return new WebRtcRoomRestartResult(false, room, false, false, null, "canceled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "restart_video error op={OpId} room={Room} elapsedMs={ElapsedMs}", opId, room, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            if (lockHeld) ReleaseVideoOpLock($"restart:{opId}");
        }
    }

    private async Task<bool> TryAcquireVideoOpLockAsync(string owner, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool acquired = await _videoRoomOpLock.WaitAsync(VideoOpLockProbeInterval, ct);
            if (!acquired)
            {
                _logger.LogWarning("video_op_lock_wait owner={Owner} blockedBy={BlockedBy}", owner, GetVideoOpOwner());
                continue;
            }

            lock (_opDiagLock)
            {
                _videoOpOwner = owner;
                _videoOpOwnerSinceUtc = DateTimeOffset.UtcNow;
            }
            return true;
        }
    }

    private void ReleaseVideoOpLock(string owner)
    {
        lock (_opDiagLock)
        {
            if (string.Equals(_videoOpOwner, owner, StringComparison.Ordinal))
            {
                _videoOpOwner = null;
                _videoOpOwnerSinceUtc = null;
            }
        }
        _videoRoomOpLock.Release();
    }

    private string GetVideoOpOwner()
    {
        lock (_opDiagLock)
        {
            if (string.IsNullOrWhiteSpace(_videoOpOwner))
            {
                return "none";
            }
            long heldMs = _videoOpOwnerSinceUtc.HasValue ? (long)(DateTimeOffset.UtcNow - _videoOpOwnerSinceUtc.Value).TotalMilliseconds : -1;
            return $"{_videoOpOwner} heldMs={heldMs}";
        }
    }

    private Task<WebRtcRoomCreateResult> CreateInternalAsync(string? room, string? qualityPreset, int? bitrateKbps, int? fps, int? imageQuality, string? captureInput, string? encoder, string? codec, bool? audioEnabled, string? audioInput, int? audioBitrateKbps, string? streamProfile, bool persist, CancellationToken ct)
    {
        _ = ct;
        RestorePersistedRoomsIfNeeded();

        string roomId = string.IsNullOrWhiteSpace(room) ? _roomIds.GenerateControlRoomId() : room.Trim();
        if (!TryNormalizeRoomId(roomId, out string normalized, out string? err))
        {
            return Task.FromResult(new WebRtcRoomCreateResult(false, roomId, false, null, err ?? "bad_room"));
        }

        string? explicitStreamProfile = NormalizeRoomStreamProfileName(streamProfile);
        if (!string.IsNullOrWhiteSpace(explicitStreamProfile) && ResolveExistingStreamProfileName(explicitStreamProfile) is null)
        {
            return Task.FromResult(new WebRtcRoomCreateResult(false, normalized, false, null, "profile_not_found"));
        }

        string? effectiveStreamProfile = ResolveExistingStreamProfileName(ResolveRoomStreamProfileForStart(normalized, streamProfile));

        ResolveEffectiveVideoSettings(
            effectiveStreamProfile,
            qualityPreset,
            bitrateKbps,
            fps,
            imageQuality,
            captureInput,
            encoder,
            codec,
            audioEnabled,
            audioInput,
            audioBitrateKbps,
            out string? normalizedPreset,
            out int? normalizedBitrate,
            out int? normalizedFps,
            out int? normalizedImageQuality,
            out string? normalizedCaptureInput,
            out string? normalizedEncoder,
            out string? normalizedCodec,
            out bool? normalizedAudioEnabled,
            out string? normalizedAudioInput,
            out int? normalizedAudioBitrate);
        var (ok, started, pid, error) = _backend.EnsureHelperStarted(normalized, normalizedPreset, normalizedBitrate, normalizedFps, normalizedImageQuality, normalizedCaptureInput, normalizedEncoder, normalizedCodec, normalizedAudioEnabled, normalizedAudioInput, normalizedAudioBitrate);
        if (ok && persist)
        {
            TouchPersistedRoom(normalized);
        }
        if (ok)
        {
            RememberKnownVideoRoom(normalized);
            RememberRoomStreamProfile(normalized, effectiveStreamProfile);
        }
        return Task.FromResult(new WebRtcRoomCreateResult(ok, normalized, started, pid, error, effectiveStreamProfile));
    }

    /// <inheritdoc />
    public Task<WebRtcRoomProfileResult> SetVideoRoomProfileAsync(string room, string? streamProfile, CancellationToken ct)
    {
        _ = ct;
        if (!TryNormalizeRoomId(room, out string normalizedRoom, out string? err))
        {
            return Task.FromResult(new WebRtcRoomProfileResult(false, room ?? string.Empty, null, err ?? "bad_room"));
        }
        if (!IsVideoRoom(normalizedRoom))
        {
            return Task.FromResult(new WebRtcRoomProfileResult(false, normalizedRoom, null, "video_room_required"));
        }

        string? normalizedProfile = NormalizeRoomStreamProfileName(streamProfile);
        if (!string.IsNullOrWhiteSpace(normalizedProfile) &&
            !_videoProfiles.GetAll().Any(p => string.Equals(p.Name, normalizedProfile, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(new WebRtcRoomProfileResult(false, normalizedRoom, null, "profile_not_found"));
        }

        lock (_videoProfilesLock)
        {
            if (string.IsNullOrWhiteSpace(normalizedProfile))
            {
                _videoRoomProfiles.Remove(normalizedRoom);
                return Task.FromResult(new WebRtcRoomProfileResult(true, normalizedRoom, null, null));
            }

            _videoRoomProfiles[normalizedRoom] = normalizedProfile;
            return Task.FromResult(new WebRtcRoomProfileResult(true, normalizedRoom, normalizedProfile, null));
        }
    }

    /// <inheritdoc />
    public Task<WebRtcRoomProfileResult> GetVideoRoomProfileAsync(string room, CancellationToken ct)
    {
        _ = ct;
        if (!TryNormalizeRoomId(room, out string normalizedRoom, out string? err))
        {
            return Task.FromResult(new WebRtcRoomProfileResult(false, room ?? string.Empty, null, err ?? "bad_room"));
        }
        if (!IsVideoRoom(normalizedRoom))
        {
            return Task.FromResult(new WebRtcRoomProfileResult(false, normalizedRoom, null, "video_room_required"));
        }

        lock (_videoProfilesLock)
        {
            _videoRoomProfiles.TryGetValue(normalizedRoom, out string? profile);
            return Task.FromResult(new WebRtcRoomProfileResult(true, normalizedRoom, profile, null));
        }
    }

    private void LoadPersistedRooms()
    {
        if (!_backend.RoomsPersistenceEnabled)
        {
            return;
        }

        string path = _backend.RoomsPersistencePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(path);
            PersistedRegistryFile? file = JsonSerializer.Deserialize<PersistedRegistryFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (file is null || file.Rooms is null || file.Rooms.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            int ttlSeconds = Math.Max(0, _backend.RoomsPersistenceTtlSeconds);
            foreach (var entry in file.Rooms)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Room))
                {
                    continue;
                }

                if (!TryNormalizeRoomId(entry.Room, out string normalized, out _))
                {
                    continue;
                }

                if (IsBuiltInRoom(normalized))
                {
                    continue;
                }
                if (IsVideoRoom(normalized))
                {
                    // Video rooms are ephemeral per session and should not be restored from disk.
                    continue;
                }

                DateTimeOffset created = entry.CreatedAtUtc == default ? now : entry.CreatedAtUtc;
                DateTimeOffset updated = entry.UpdatedAtUtc == default ? created : entry.UpdatedAtUtc;
                if (ttlSeconds > 0 && (now - updated).TotalSeconds >= ttlSeconds)
                {
                    continue;
                }

                _persistedRooms[normalized] = new PersistedRoomState(created, updated);
            }
        }
        catch
        {
            // Corrupt or partial persistence file should not affect runtime room operations.
        }
    }

    private void RestorePersistedRoomsIfNeeded()
    {
        if (!_backend.RoomsPersistenceEnabled)
        {
            return;
        }

        string[] toRestore;
        lock (_persistLock)
        {
            if (_restoreAttempted)
            {
                return;
            }

            _restoreAttempted = true;
            toRestore = _persistedRooms.Keys
                .Where(r => !IsBuiltInRoom(r))
                .ToArray();
        }

        foreach (string room in toRestore)
        {
            var res = _backend.EnsureHelperStarted(room);
            if (!res.ok)
            {
                // Keep persisted record; subsequent manual connect/create can retry helper startup.
                continue;
            }

            TouchPersistedRoom(room);
        }
    }

    private void TouchPersistedRoom(string room)
    {
        if (!_backend.RoomsPersistenceEnabled || IsBuiltInRoom(room) || IsVideoRoom(room))
        {
            return;
        }

        lock (_persistLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_persistedRooms.TryGetValue(room, out var existing))
            {
                _persistedRooms[room] = existing with { UpdatedAtUtc = now };
            }
            else
            {
                _persistedRooms[room] = new PersistedRoomState(now, now);
            }

            SavePersistedRoomsUnsafe();
        }
    }

    private void RemovePersistedRoom(string room)
    {
        if (!_backend.RoomsPersistenceEnabled)
        {
            return;
        }

        lock (_persistLock)
        {
            if (_persistedRooms.Remove(room))
            {
                SavePersistedRoomsUnsafe();
            }
        }
    }

    private void RefreshPersistenceFromActivity(IReadOnlyDictionary<string, int> peers, IReadOnlySet<string> helpers)
    {
        if (!_backend.RoomsPersistenceEnabled)
        {
            return;
        }

        lock (_persistLock)
        {
            if (_persistedRooms.Count == 0)
            {
                return;
            }

            bool changed = false;
            var now = DateTimeOffset.UtcNow;
            foreach (var room in _persistedRooms.Keys.ToArray())
            {
                if (IsBuiltInRoom(room))
                {
                    continue;
                }

                bool hasHelper = helpers.Contains(room);
                bool hasPeers = peers.TryGetValue(room, out int count) && count > 0;
                if (!hasHelper && !hasPeers)
                {
                    continue;
                }

                var current = _persistedRooms[room];
                if ((now - current.UpdatedAtUtc).TotalSeconds < PersistActivityWriteMinSeconds)
                {
                    continue;
                }

                _persistedRooms[room] = current with { UpdatedAtUtc = now };
                changed = true;
            }

            if (changed)
            {
                SavePersistedRoomsUnsafe();
            }
        }
    }

    private void CleanupPersistedRooms(IReadOnlyDictionary<string, int> peers, IReadOnlySet<string> helpers)
    {
        if (!_backend.RoomsPersistenceEnabled)
        {
            return;
        }

        int ttlSeconds = Math.Max(0, _backend.RoomsPersistenceTtlSeconds);
        if (ttlSeconds == 0)
        {
            return;
        }

        lock (_persistLock)
        {
            if (_persistedRooms.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            bool changed = false;
            foreach (var room in _persistedRooms.Keys.ToArray())
            {
                if (IsBuiltInRoom(room))
                {
                    _persistedRooms.Remove(room);
                    changed = true;
                    continue;
                }

                bool hasHelper = helpers.Contains(room);
                bool hasPeers = peers.TryGetValue(room, out int count) && count > 0;
                if (hasHelper || hasPeers)
                {
                    continue;
                }

                var state = _persistedRooms[room];
                if ((now - state.UpdatedAtUtc).TotalSeconds < ttlSeconds)
                {
                    continue;
                }

                _persistedRooms.Remove(room);
                changed = true;
            }

            if (changed)
            {
                SavePersistedRoomsUnsafe();
            }
        }
    }

    private IReadOnlyList<string> GetPersistedRoomIdsSnapshot()
    {
        if (!_backend.RoomsPersistenceEnabled)
        {
            return Array.Empty<string>();
        }

        lock (_persistLock)
        {
            return _persistedRooms.Keys.ToArray();
        }
    }

    private IReadOnlyList<string> GetKnownVideoRoomsSnapshot()
    {
        lock (_videoProfilesLock)
        {
            return _videoRoomProfiles.Keys
                .Where(IsVideoRoom)
                .ToArray();
        }
    }

    private void SavePersistedRoomsUnsafe()
    {
        if (!_backend.RoomsPersistenceEnabled)
        {
            return;
        }

        try
        {
            string path = _backend.RoomsPersistencePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var file = new PersistedRegistryFile(
                PersistFileVersion,
                _persistedRooms
                    .Select(kvp => new PersistedRoomEntry(kvp.Key, kvp.Value.CreatedAtUtc, kvp.Value.UpdatedAtUtc))
                    .OrderBy(r => r.Room, StringComparer.OrdinalIgnoreCase)
                    .ToArray());

            string json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            string tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Copy(tmpPath, path, overwrite: true);
            File.Delete(tmpPath);
        }
        catch
        {
            // Persistence failures are non-fatal; runtime room behavior remains in-memory.
        }
    }

    private static bool IsBuiltInRoom(string room)
    {
        return string.Equals(room, "control", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(room, "video", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeQualityPreset(string? qualityPreset)
    {
        if (string.IsNullOrWhiteSpace(qualityPreset))
        {
            return null;
        }

        return qualityPreset.Trim().ToLowerInvariant();
    }

    private static string? NormalizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return null;
        }

        string c = codec.Trim().ToLowerInvariant();
        return c switch
        {
            "auto" or "vp8" or "h264" => c,
            _ => null
        };
    }

    private static int? NormalizeImageQuality(int? imageQuality)
    {
        if (imageQuality is null)
        {
            return null;
        }

        if (imageQuality < 1 || imageQuality > 100)
        {
            return null;
        }

        return imageQuality.Value;
    }

    private static string? NormalizeEncoder(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder))
        {
            return null;
        }

        string e = encoder.Trim().ToLowerInvariant();
        return e switch
        {
            "auto" or "cpu" or "hw" or "nvenc" or "amf" or "qsv" or "v4l2m2m" or "vaapi" => e,
            _ => null
        };
    }

    private static string? NormalizeAudioInput(string? audioInput)
    {
        if (string.IsNullOrWhiteSpace(audioInput))
        {
            return null;
        }

        return audioInput.Trim();
    }

    private static int? NormalizeAudioBitrateKbps(int? audioBitrateKbps)
    {
        if (audioBitrateKbps is null)
        {
            return null;
        }

        if (audioBitrateKbps < 16 || audioBitrateKbps > 512)
        {
            return null;
        }

        return audioBitrateKbps.Value;
    }

    private static int? NormalizeBitrateKbps(int? bitrateKbps)
    {
        if (bitrateKbps is null)
        {
            return null;
        }
        if (bitrateKbps < 200 || bitrateKbps > 12000)
        {
            return null;
        }
        return bitrateKbps.Value;
    }

    private static int? NormalizeFps(int? fps)
    {
        if (fps is null)
        {
            return null;
        }
        if (fps < 5 || fps > 60)
        {
            return null;
        }
        return fps.Value;
    }

    private static string? NormalizeRoomStreamProfileName(string? streamProfile)
    {
        if (streamProfile is null)
        {
            return null;
        }

        string normalized = streamProfile.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private string? ResolveExistingStreamProfileName(string? streamProfile)
    {
        string? normalized = NormalizeRoomStreamProfileName(streamProfile);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        VideoProfileConfig? profile = _videoProfiles.GetAll()
            .FirstOrDefault(p => string.Equals(p.Name, normalized, StringComparison.OrdinalIgnoreCase));
        return profile?.Name;
    }

    private string? ResolveRoomStreamProfileForStart(string room, string? streamProfile)
    {
        string? requested = NormalizeRoomStreamProfileName(streamProfile);
        if (!IsVideoRoom(room))
        {
            return requested;
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested;
        }

        lock (_videoProfilesLock)
        {
            return _videoRoomProfiles.TryGetValue(room, out string? stored) ? stored : null;
        }
    }

    private void RememberRoomStreamProfile(string room, string? streamProfile)
    {
        if (!IsVideoRoom(room))
        {
            return;
        }

        // Null means no explicit change requested by caller.
        if (streamProfile is null)
        {
            return;
        }

        string? normalized = NormalizeRoomStreamProfileName(streamProfile);
        lock (_videoProfilesLock)
        {
            if (string.IsNullOrWhiteSpace(normalized))
            {
                _videoRoomProfiles.Remove(room);
            }
            else
            {
                _videoRoomProfiles[room] = normalized;
            }
        }
    }

    private void RememberKnownVideoRoom(string room)
    {
        if (!IsVideoRoom(room))
        {
            return;
        }

        lock (_videoProfilesLock)
        {
            _videoRoomProfiles.TryAdd(room, null);
        }
    }

    private void ResolveEffectiveVideoSettings(
        string? streamProfile,
        string? qualityPreset,
        int? bitrateKbps,
        int? fps,
        int? imageQuality,
        string? captureInput,
        string? encoder,
        string? codec,
        bool? audioEnabled,
        string? audioInput,
        int? audioBitrateKbps,
        out string? normalizedPreset,
        out int? normalizedBitrate,
        out int? normalizedFps,
        out int? normalizedImageQuality,
        out string? normalizedCaptureInput,
        out string? normalizedEncoder,
        out string? normalizedCodec,
        out bool? normalizedAudioEnabled,
        out string? normalizedAudioInput,
        out int? normalizedAudioBitrate)
    {
        // Start with base profile values (if requested), then apply explicit request overrides.
        string? basePreset = null;
        int? baseBitrate = null;
        int? baseFps = null;
        string? baseCaptureInput = null;
        string? baseEncoder = null;
        string? baseCodec = null;
        bool? baseAudioEnabled = null;
        string? baseAudioInput = null;
        int? baseAudioBitrate = null;

        if (!string.IsNullOrWhiteSpace(streamProfile))
        {
            string wanted = streamProfile.Trim();
            VideoProfileConfig? profile = _videoProfiles.GetAll()
                .FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (profile is not null)
            {
                ParseProfileArgs(profile.Args, out baseCodec, out baseEncoder, out baseBitrate, out baseFps, out baseCaptureInput);
                basePreset = InferQualityPreset(baseCodec, baseBitrate);
                baseAudioEnabled = profile.AudioEnabled;
                baseAudioInput = profile.AudioInput;
                baseAudioBitrate = profile.AudioBitrateKbps;
            }
        }

        normalizedPreset = NormalizeQualityPreset(qualityPreset) ?? basePreset;
        normalizedBitrate = NormalizeBitrateKbps(bitrateKbps) ?? NormalizeBitrateKbps(baseBitrate);
        normalizedFps = NormalizeFps(fps) ?? NormalizeFps(baseFps);
        normalizedImageQuality = NormalizeImageQuality(imageQuality);
        normalizedCaptureInput = string.IsNullOrWhiteSpace(captureInput) ? baseCaptureInput : captureInput.Trim();
        normalizedEncoder = NormalizeEncoder(encoder) ?? NormalizeEncoder(baseEncoder);
        normalizedCodec = NormalizeCodec(codec) ?? NormalizeCodec(baseCodec);
        normalizedAudioEnabled = audioEnabled ?? baseAudioEnabled;
        normalizedAudioInput = NormalizeAudioInput(audioInput) ?? NormalizeAudioInput(baseAudioInput);
        normalizedAudioBitrate = NormalizeAudioBitrateKbps(audioBitrateKbps) ?? NormalizeAudioBitrateKbps(baseAudioBitrate);
    }

    private static void ParseProfileArgs(
        string? args,
        out string? codec,
        out string? encoder,
        out int? bitrateKbps,
        out int? fps,
        out string? captureInput)
    {
        codec = null;
        encoder = null;
        bitrateKbps = null;
        fps = null;
        captureInput = null;
        string text = args ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Match cMatch = Regex.Match(text, @"-c:v\s+([^\s]+)", RegexOptions.IgnoreCase);
        if (cMatch.Success)
        {
            string c = cMatch.Groups[1].Value.Trim().ToLowerInvariant();
            if (c == "libvpx")
            {
                codec = "vp8";
                encoder = "cpu";
            }
            else if (c == "libx264")
            {
                codec = "h264";
                encoder = "cpu";
            }
            else if (c.Contains("nvenc", StringComparison.Ordinal))
            {
                codec = "h264";
                encoder = "nvenc";
            }
            else if (c.Contains("amf", StringComparison.Ordinal))
            {
                codec = "h264";
                encoder = "amf";
            }
            else if (c.Contains("qsv", StringComparison.Ordinal))
            {
                codec = "h264";
                encoder = "qsv";
            }
            else if (c.Contains("v4l2m2m", StringComparison.Ordinal))
            {
                codec = "h264";
                encoder = "v4l2m2m";
            }
        }

        Match bMatch = Regex.Match(text, @"-b:v\s+(\d+)k\b", RegexOptions.IgnoreCase);
        if (bMatch.Success && int.TryParse(bMatch.Groups[1].Value, out int kb))
        {
            bitrateKbps = kb;
        }

        Match fpsMatch = Regex.Match(text, @"-r\s+(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, out double f))
        {
            fps = (int)Math.Round(f);
        }

        Match quotedInput = Regex.Match(text, "-i\\s+\"video=([^\"]+)\"", RegexOptions.IgnoreCase);
        if (quotedInput.Success)
        {
            captureInput = $"video={quotedInput.Groups[1].Value}";
        }
        else
        {
            Match plainInput = Regex.Match(text, @"-i\s+video=([^\s]+)", RegexOptions.IgnoreCase);
            if (plainInput.Success)
            {
                captureInput = $"video={plainInput.Groups[1].Value}";
            }
        }
    }

    private static string? InferQualityPreset(string? codec, int? bitrateKbps)
    {
        if (bitrateKbps is null || bitrateKbps <= 0)
        {
            return null;
        }
        if (bitrateKbps <= 1000) return "low-latency";
        if (bitrateKbps <= 1800) return "balanced";
        if (bitrateKbps <= 2600) return "high";
        if (string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase)) return "optimal";
        return "high";
    }

    private static bool IsVideoRoom(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            return false;
        }

        return string.Equals(room, "video", StringComparison.OrdinalIgnoreCase) ||
               room.StartsWith("video-", StringComparison.OrdinalIgnoreCase) ||
               room.StartsWith("hb-v-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeRoomId(string room, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(room))
        {
            error = "room_required";
            return false;
        }

        string r = room.Trim();
        if (r.Length > 64)
        {
            error = "room_too_long";
            return false;
        }

        foreach (char ch in r)
        {
            bool ok = (ch >= 'a' && ch <= 'z') ||
                      (ch >= 'A' && ch <= 'Z') ||
                      (ch >= '0' && ch <= '9') ||
                      ch == '_' || ch == '-';
            if (!ok)
            {
                error = "room_invalid_chars";
                return false;
            }
        }

        normalized = r;
        return true;
    }

    private sealed record PersistedRoomState(DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

    private sealed record PersistedRegistryFile(int Version, IReadOnlyList<PersistedRoomEntry> Rooms);

    private sealed record PersistedRoomEntry(string Room, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
}
