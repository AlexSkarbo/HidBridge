using HidControl.Application.Abstractions;

namespace HidControlServer.Services;

/// <summary>
/// Server-layer adapter that exposes WebRTC control-plane runtime details to Infrastructure services.
/// </summary>
public sealed class WebRtcBackendAdapter : IWebRtcBackend
{
    private readonly Options _opt;
    private readonly WebRtcControlPeerSupervisor _controlSup;
    private readonly WebRtcVideoPeerSupervisor _videoSup;
    private readonly HidUartClient _uart;
    private readonly IWebRtcSignalingService _signaling;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="controlSup">Control helper supervisor.</param>
    /// <param name="videoSup">Video helper supervisor.</param>
    /// <param name="uart">UART client.</param>
    /// <param name="signaling">WebRTC signaling room state service.</param>
    public WebRtcBackendAdapter(
        Options opt,
        WebRtcControlPeerSupervisor controlSup,
        WebRtcVideoPeerSupervisor videoSup,
        HidUartClient uart,
        IWebRtcSignalingService signaling)
    {
        _opt = opt;
        _controlSup = controlSup;
        _videoSup = videoSup;
        _uart = uart;
        _signaling = signaling;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, int> GetRoomPeerCountsSnapshot() => _signaling.GetRoomPeerCountsSnapshot();

    /// <inheritdoc />
    public IReadOnlySet<string> GetHelperRoomsSnapshot()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in _controlSup.GetHelpersSnapshot())
        {
            try
            {
                var prop = obj.GetType().GetProperty("room");
                string? room = prop?.GetValue(obj)?.ToString();
                if (!string.IsNullOrWhiteSpace(room)) set.Add(room);
            }
            catch { }
        }
        foreach (var obj in _videoSup.GetHelpersSnapshot())
        {
            try
            {
                var prop = obj.GetType().GetProperty("room");
                string? room = prop?.GetValue(obj)?.ToString();
                if (!string.IsNullOrWhiteSpace(room)) set.Add(room);
            }
            catch { }
        }
        return set;
    }

    /// <inheritdoc />
    public (bool ok, bool started, int? pid, string? error) EnsureHelperStarted(string room)
        => IsVideoRoom(room) ? _videoSup.EnsureStarted(room) : _controlSup.EnsureStarted(room);

    /// <inheritdoc />
    public (bool ok, bool stopped, string? error) StopHelper(string room)
        => IsVideoRoom(room) ? _videoSup.StopRoom(room) : _controlSup.StopRoom(room);

    /// <inheritdoc />
    public string? GetDeviceIdHex() => _uart.GetDeviceIdHex();

    /// <inheritdoc />
    public string StunUrl => _opt.WebRtcControlPeerStun;

    /// <inheritdoc />
    public IReadOnlyList<string> TurnUrls => _opt.WebRtcTurnUrls;

    /// <inheritdoc />
    public string TurnSharedSecret => _opt.WebRtcTurnSharedSecret;

    /// <inheritdoc />
    public int TurnTtlSeconds => _opt.WebRtcTurnTtlSeconds;

    /// <inheritdoc />
    public string TurnUsername => _opt.WebRtcTurnUsername;

    /// <inheritdoc />
    public int ClientJoinTimeoutMs => _opt.WebRtcClientJoinTimeoutMs;

    /// <inheritdoc />
    public int ClientConnectTimeoutMs => _opt.WebRtcClientConnectTimeoutMs;

    /// <inheritdoc />
    public int RoomsCleanupIntervalSeconds => _opt.WebRtcRoomsCleanupIntervalSeconds;

    /// <inheritdoc />
    public int RoomIdleStopSeconds => _opt.WebRtcRoomIdleStopSeconds;

    /// <inheritdoc />
    public int RoomsMaxHelpers => _opt.WebRtcRoomsMaxHelpers;

    /// <inheritdoc />
    public bool RoomsPersistenceEnabled => _opt.WebRtcRoomsPersistEnabled;

    /// <inheritdoc />
    public string RoomsPersistencePath => _opt.WebRtcRoomsStorePath;

    /// <inheritdoc />
    public int RoomsPersistenceTtlSeconds => _opt.WebRtcRoomsPersistTtlSeconds;

    private static bool IsVideoRoom(string room)
    {
        if (string.IsNullOrWhiteSpace(room)) return false;
        if (string.Equals(room, "video", StringComparison.OrdinalIgnoreCase)) return true;
        return room.StartsWith("hb-v-", StringComparison.OrdinalIgnoreCase) ||
               room.StartsWith("video-", StringComparison.OrdinalIgnoreCase);
    }
}
