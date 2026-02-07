using HidControl.Application.Abstractions;
using HidControlServer.Endpoints.Ws;

namespace HidControlServer.Services;

/// <summary>
/// Server-layer adapter that exposes WebRTC control-plane runtime details to Infrastructure services.
/// </summary>
public sealed class WebRtcBackendAdapter : IWebRtcBackend
{
    private readonly Options _opt;
    private readonly WebRtcControlPeerSupervisor _sup;
    private readonly HidUartClient _uart;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="sup">Supervisor.</param>
    /// <param name="uart">UART client.</param>
    public WebRtcBackendAdapter(Options opt, WebRtcControlPeerSupervisor sup, HidUartClient uart)
    {
        _opt = opt;
        _sup = sup;
        _uart = uart;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, int> GetRoomPeerCountsSnapshot() => WebRtcWsEndpoints.GetRoomPeerCountsSnapshot();

    /// <inheritdoc />
    public IReadOnlySet<string> GetHelperRoomsSnapshot()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in _sup.GetHelpersSnapshot())
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
    public (bool ok, bool started, int? pid, string? error) EnsureHelperStarted(string room) => _sup.EnsureStarted(room);

    /// <inheritdoc />
    public (bool ok, bool stopped, string? error) StopHelper(string room) => _sup.StopRoom(room);

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
}

