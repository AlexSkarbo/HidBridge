using System.Collections.Concurrent;
using HidControl.Application.Abstractions;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Default in-memory implementation of WebRTC signaling room state.
/// </summary>
public sealed class WebRtcSignalingService : IWebRtcSignalingService
{
    private const int DefaultRoomMaxPeers = 2;
    private readonly ConcurrentDictionary<string, RoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, int> GetRoomPeerCountsSnapshot()
    {
        return _rooms.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ClientIds.Count, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public WebRtcRoomNormalizationResult NormalizeRoom(string? room)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            return new WebRtcRoomNormalizationResult(false, null, "room_required");
        }

        string normalized = room.Trim();
        if (normalized.Length > 64)
        {
            return new WebRtcRoomNormalizationResult(false, null, "room_too_long");
        }

        foreach (char ch in normalized)
        {
            bool ok = (ch >= 'a' && ch <= 'z') ||
                      (ch >= 'A' && ch <= 'Z') ||
                      (ch >= '0' && ch <= '9') ||
                      ch == '_' || ch == '-';
            if (!ok)
            {
                return new WebRtcRoomNormalizationResult(false, null, "room_invalid_chars");
            }
        }

        return new WebRtcRoomNormalizationResult(true, normalized, null);
    }

    /// <inheritdoc />
    public WebRtcJoinResult TryJoin(string room, string clientId)
    {
        WebRtcRoomNormalizationResult normalized = NormalizeRoom(room);
        if (!normalized.Ok || string.IsNullOrWhiteSpace(normalized.Room))
        {
            return new WebRtcJoinResult(false, 0, normalized.Error ?? "bad_room");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new WebRtcJoinResult(false, 0, "client_required");
        }

        string roomId = normalized.Room;
        RoomState state = _rooms.GetOrAdd(roomId, _ => new RoomState());
        lock (state.Gate)
        {
            if (state.ClientIds.ContainsKey(clientId))
            {
                return new WebRtcJoinResult(true, state.ClientIds.Count, null);
            }

            if (state.ClientIds.Count >= DefaultRoomMaxPeers)
            {
                return new WebRtcJoinResult(false, state.ClientIds.Count, "room_full");
            }

            state.ClientIds[clientId] = 0;
            return new WebRtcJoinResult(true, state.ClientIds.Count, null);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetPeerIds(string room, string? excludeClientId = null)
    {
        if (!_rooms.TryGetValue(room, out RoomState? state))
        {
            return Array.Empty<string>();
        }

        return state.ClientIds.Keys
            .Where(id => !string.Equals(id, excludeClientId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <inheritdoc />
    public bool IsJoined(string room, string clientId)
    {
        if (!_rooms.TryGetValue(room, out RoomState? state))
        {
            return false;
        }

        return state.ClientIds.ContainsKey(clientId);
    }

    /// <inheritdoc />
    public void Leave(string room, string clientId)
    {
        if (!_rooms.TryGetValue(room, out RoomState? state))
        {
            return;
        }

        bool shouldRemoveRoom = false;
        lock (state.Gate)
        {
            state.ClientIds.TryRemove(clientId, out _);
            shouldRemoveRoom = state.ClientIds.IsEmpty;
        }

        if (shouldRemoveRoom)
        {
            _rooms.TryRemove(room, out _);
        }
    }

    private sealed class RoomState
    {
        public object Gate { get; } = new();
        public ConcurrentDictionary<string, byte> ClientIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
