using HidControl.Application.Abstractions;
using System.Text.Json;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Default implementation of WebRTC room lifecycle service.
/// </summary>
public sealed class WebRtcRoomsService : IWebRtcRoomsService
{
    private const int PersistFileVersion = 1;
    private const int PersistActivityWriteMinSeconds = 30;

    private readonly IWebRtcBackend _backend;
    private readonly IWebRtcRoomIdService _roomIds;
    private readonly object _persistLock = new();
    private readonly Dictionary<string, PersistedRoomState> _persistedRooms = new(StringComparer.OrdinalIgnoreCase);
    private bool _restoreAttempted;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="backend">Backend.</param>
    public WebRtcRoomsService(IWebRtcBackend backend, IWebRtcRoomIdService roomIds)
    {
        _backend = backend;
        _roomIds = roomIds;
        LoadPersistedRooms();
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
        return CreateInternalAsync(room, qualityPreset: null, bitrateKbps: null, fps: null, persist: true, ct);
    }

    /// <inheritdoc />
    public Task<WebRtcRoomCreateResult> CreateVideoAsync(string? room, string? qualityPreset, int? bitrateKbps, int? fps, CancellationToken ct)
    {
        return CreateInternalAsync(room, qualityPreset, bitrateKbps, fps, persist: false, ct);
    }

    /// <inheritdoc />
    public async Task<WebRtcRoomDeleteResult> DeleteAsync(string room, CancellationToken ct)
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

    private Task<WebRtcRoomCreateResult> CreateInternalAsync(string? room, string? qualityPreset, int? bitrateKbps, int? fps, bool persist, CancellationToken ct)
    {
        _ = ct;
        RestorePersistedRoomsIfNeeded();

        string roomId = string.IsNullOrWhiteSpace(room) ? _roomIds.GenerateControlRoomId() : room.Trim();
        if (!TryNormalizeRoomId(roomId, out string normalized, out string? err))
        {
            return Task.FromResult(new WebRtcRoomCreateResult(false, roomId, false, null, err ?? "bad_room"));
        }

        string? normalizedPreset = NormalizeQualityPreset(qualityPreset);
        var (ok, started, pid, error) = _backend.EnsureHelperStarted(normalized, normalizedPreset, bitrateKbps, fps);
        if (ok && persist)
        {
            TouchPersistedRoom(normalized);
        }
        return Task.FromResult(new WebRtcRoomCreateResult(ok, normalized, started, pid, error));
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
