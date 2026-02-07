using HidControl.Application.Abstractions;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Default implementation of WebRTC room lifecycle service.
/// </summary>
public sealed class WebRtcRoomsService : IWebRtcRoomsService
{
    private readonly IWebRtcBackend _backend;
    private readonly IWebRtcRoomIdService _roomIds;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="backend">Backend.</param>
    public WebRtcRoomsService(IWebRtcBackend backend, IWebRtcRoomIdService roomIds)
    {
        _backend = backend;
        _roomIds = roomIds;
    }

    /// <inheritdoc />
    public Task<WebRtcRoomsSnapshot> ListAsync(CancellationToken ct)
    {
        _ = ct;
        var peers = _backend.GetRoomPeerCountsSnapshot();
        var helperRooms = _backend.GetHelperRoomsSnapshot();

        var allRooms = new HashSet<string>(peers.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var hr in helperRooms) allRooms.Add(hr);

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
        _ = ct;
        string roomId = string.IsNullOrWhiteSpace(room) ? _roomIds.GenerateControlRoomId() : room.Trim();
        if (!TryNormalizeRoomId(roomId, out string normalized, out string? err))
        {
            return Task.FromResult(new WebRtcRoomCreateResult(false, roomId, false, null, err ?? "bad_room"));
        }

        var (ok, started, pid, error) = _backend.EnsureHelperStarted(normalized);
        return Task.FromResult(new WebRtcRoomCreateResult(ok, normalized, started, pid, error));
    }

    /// <inheritdoc />
    public Task<WebRtcRoomDeleteResult> DeleteAsync(string room, CancellationToken ct)
    {
        _ = ct;
        if (string.Equals(room, "control", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new WebRtcRoomDeleteResult(false, room, false, "cannot_delete_control"));
        }
        if (string.Equals(room, "video", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new WebRtcRoomDeleteResult(false, room, false, "cannot_delete_video"));
        }

        var (ok, stopped, error) = _backend.StopHelper(room);
        return Task.FromResult(new WebRtcRoomDeleteResult(ok, room, stopped, error));
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
}
