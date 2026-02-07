using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HidControlServer;
using HidControlServer.Endpoints.Ws;
using HidControlServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HidControlServer.Endpoints.System;

/// <summary>
/// Registers System endpoints.
/// </summary>
public static class SystemEndpoints
{
    /// <summary>
    /// Maps system endpoints.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapSystemEndpoints(this WebApplication app)
    {
        var appState = app.Services.GetRequiredService<AppState>();

        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        app.MapGet("/version", () =>
        {
            string? repoVersion = DeviceDiagnostics.FindRepoVersion();
            string? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            return Results.Ok(new
            {
                ok = true,
                repoVersion,
                assemblyVersion
            });
        });

        app.MapGet("/config", (Options opt) =>
        {
            return Results.Ok(new
            {
                ok = true,
                serialPort = opt.SerialPort,
                serialPortConfigured = appState.SerialConfigured,
                serialPortResolved = appState.SerialResolved,
                serialAuto = appState.SerialAutoEnabled,
                serialMatch = appState.SerialMatch,
                serialAutoStatus = appState.SerialAutoStatus,
                baud = opt.Baud,
                url = opt.Url,
                effectiveUrls = VideoUrlService.BuildBindUrls(opt),
                bindAll = opt.BindAll,
                mouseMappingDb = opt.MouseMappingDb,
                videoSourcesCount = opt.VideoSources.Count,
                ffmpegPath = opt.FfmpegPath,
                ffmpegAutoStart = opt.FfmpegAutoStart,
                ffmpegWatchdogEnabled = opt.FfmpegWatchdogEnabled,
                ffmpegWatchdogIntervalMs = opt.FfmpegWatchdogIntervalMs,
                ffmpegRestartDelayMs = opt.FfmpegRestartDelayMs,
                ffmpegMaxRestarts = opt.FfmpegMaxRestarts,
                ffmpegRestartWindowMs = opt.FfmpegRestartWindowMs,
                videoRtspPort = opt.VideoRtspPort,
                videoSrtBasePort = opt.VideoSrtBasePort,
                videoSrtLatencyMs = opt.VideoSrtLatencyMs,
                videoRtmpPort = opt.VideoRtmpPort,
                videoHlsDir = opt.VideoHlsDir,
                videoLlHls = opt.VideoLlHls,
                videoHlsSegmentSeconds = opt.VideoHlsSegmentSeconds,
                videoHlsListSize = opt.VideoHlsListSize,
                videoHlsDeleteSegments = opt.VideoHlsDeleteSegments,
                videoLogDir = opt.VideoLogDir,
                videoCaptureAutoStopFfmpeg = opt.VideoCaptureAutoStopFfmpeg,
                videoCaptureRetryCount = opt.VideoCaptureRetryCount,
                videoCaptureRetryDelayMs = opt.VideoCaptureRetryDelayMs,
                videoCaptureLockTimeoutMs = opt.VideoCaptureLockTimeoutMs,
                videoSecondaryCaptureEnabled = opt.VideoSecondaryCaptureEnabled,
                videoKillOrphanFfmpeg = opt.VideoKillOrphanFfmpeg,
                videoProfilesCount = opt.VideoProfiles.Count,
                activeVideoProfile = opt.ActiveVideoProfile,
                pgConnectionStringSet = !string.IsNullOrWhiteSpace(opt.PgConnectionString),
                migrateSqliteToPg = opt.MigrateSqliteToPg,
                mouseReportLen = opt.MouseReportLen,
                injectQueueCapacity = opt.InjectQueueCapacity,
                injectDropThreshold = opt.InjectDropThreshold,
                injectTimeoutMs = opt.InjectTimeoutMs,
                injectRetries = opt.InjectRetries,
                mouseMoveTimeoutMs = opt.MouseMoveTimeoutMs,
                mouseMoveDropIfBusy = opt.MouseMoveDropIfBusy,
                mouseMoveAllowZero = opt.MouseMoveAllowZero,
                mouseWheelTimeoutMs = opt.MouseWheelTimeoutMs,
                mouseWheelDropIfBusy = opt.MouseWheelDropIfBusy,
                mouseWheelAllowZero = opt.MouseWheelAllowZero,
                keyboardInjectTimeoutMs = opt.KeyboardInjectTimeoutMs,
                keyboardInjectRetries = opt.KeyboardInjectRetries,
                masterSecretSet = !string.IsNullOrWhiteSpace(opt.MasterSecret),
                mouseItfSel = opt.MouseItfSel,
                keyboardItfSel = opt.KeyboardItfSel,
                mouseTypeName = opt.MouseTypeName,
                keyboardTypeName = opt.KeyboardTypeName,
                devicesAutoMuteLogs = opt.DevicesAutoMuteLogs,
                devicesAutoRefreshMs = opt.DevicesAutoRefreshMs,
                devicesIncludeReportDesc = opt.DevicesIncludeReportDesc,
                uartHmacKeySet = !string.IsNullOrWhiteSpace(opt.UartHmacKey),
                mouseLeftMask = opt.MouseLeftMask,
                mouseRightMask = opt.MouseRightMask,
                mouseMiddleMask = opt.MouseMiddleMask,
                mouseBackMask = opt.MouseBackMask,
                mouseForwardMask = opt.MouseForwardMask,
                tokenEnabled = !string.IsNullOrWhiteSpace(opt.Token)
            });
        });

        app.MapGet("/logs/last", (int? limit) =>
        {
            var items = ServerEventLog.GetRecent(limit);
            return Results.Ok(new { ok = true, items });
        });

        // WebRTC ICE servers for browser clients. Useful for TURN REST ephemeral credentials.
        //
        // Note: This endpoint is under `/status/*` so it can be accessed when token auth is enabled
        // (same auth rules as `/ws/*`).
        app.MapGet("/status/webrtc/ice", (Options opt) =>
        {
            var iceServers = new List<object>();
            if (!string.IsNullOrWhiteSpace(opt.WebRtcControlPeerStun))
            {
                iceServers.Add(new { urls = new[] { opt.WebRtcControlPeerStun } });
            }

            // TURN REST credentials (coturn: --use-auth-secret --static-auth-secret=...).
            if (opt.WebRtcTurnUrls.Count > 0 && !string.IsNullOrWhiteSpace(opt.WebRtcTurnSharedSecret))
            {
                int ttl = Math.Clamp(opt.WebRtcTurnTtlSeconds, 60, 24 * 60 * 60);
                long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl;
                string userPart = string.IsNullOrWhiteSpace(opt.WebRtcTurnUsername) ? "hidbridge" : opt.WebRtcTurnUsername.Trim();
                string username = $"{expires}:{userPart}";

                byte[] key = Encoding.UTF8.GetBytes(opt.WebRtcTurnSharedSecret);
                byte[] msg = Encoding.UTF8.GetBytes(username);
                using var hmac = new HMACSHA1(key);
                string credential = Convert.ToBase64String(hmac.ComputeHash(msg));

                iceServers.Add(new
                {
                    urls = opt.WebRtcTurnUrls,
                    username,
                    credential,
                    credentialType = "password"
                });

                return Results.Ok(new { ok = true, ttlSeconds = ttl, iceServers });
            }

            return Results.Ok(new { ok = true, iceServers });
        });

        app.MapGet("/status/webrtc/rooms", (WebRtcControlPeerSupervisor sup) =>
        {
            var peers = WebRtcWsEndpoints.GetRoomPeerCountsSnapshot();
            var helpers = sup.GetHelpersSnapshot();

            var helperRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in helpers)
            {
                string? r = h.GetType().GetProperty("room")?.GetValue(h)?.ToString();
                if (!string.IsNullOrWhiteSpace(r)) helperRooms.Add(r);
            }

            var allRooms = new HashSet<string>(peers.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var hr in helperRooms) allRooms.Add(hr);

            var rooms = allRooms
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .Select(r => new
                {
                    room = r,
                    peers = peers.TryGetValue(r, out int c) ? c : 0,
                    hasHelper = helperRooms.Contains(r),
                    isControl = string.Equals(r, "control", StringComparison.OrdinalIgnoreCase)
                })
                .ToArray();

            return Results.Ok(new { ok = true, rooms });
        });

        app.MapPost("/status/webrtc/rooms", async (HttpRequest req, WebRtcControlPeerSupervisor sup) =>
        {
            string? roomId = null;
            try
            {
                var body = await req.ReadFromJsonAsync<Dictionary<string, string?>>();
                if (body is not null && body.TryGetValue("room", out var r))
                {
                    roomId = r;
                }
            }
            catch { }

            roomId = string.IsNullOrWhiteSpace(roomId) ? GenerateRoomId() : roomId.Trim();
            if (!TryNormalizeRoomIdForApi(roomId, out string normalized, out string? err))
            {
                return Results.Ok(new { ok = false, error = err ?? "bad_room" });
            }

            var (ok, started, pid, error) = sup.EnsureStarted(normalized);
            return Results.Ok(new { ok, room = normalized, started, pid, error });
        });

        app.MapDelete("/status/webrtc/rooms/{room}", (string room, WebRtcControlPeerSupervisor sup) =>
        {
            if (string.Equals(room, "control", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok(new { ok = false, error = "cannot_delete_control" });
            }

            var (ok, stopped, error) = sup.StopRoom(room);
            return Results.Ok(new { ok, room, stopped, error });
        });

        app.MapGet("/serial/ports", () =>
        {
            var list = SerialPortService.ListSerialPortCandidates();
            return Results.Ok(new { ok = true, ports = list });
        });

        app.MapGet("/serial/probe", (Options opt, int? timeoutMs) =>
        {
            int timeout = Math.Clamp(timeoutMs ?? 600, 100, 3000);
            var list = SerialPortService.ProbeSerialDevices(opt.Baud, DeviceKeyService.GetBootstrapKey(opt), opt.InjectQueueCapacity, opt.InjectDropThreshold, timeout);
            return Results.Ok(new { ok = true, timeoutMs = timeout, ports = list });
        });

        app.MapGet("/stats", (Options opt, HidUartClient uart, KeyboardState keyboard, MouseState mouse) =>
        {
            string hmacMode = uart.IsBootstrapForced() ? "bootstrap" : (uart.HasDerivedKey() ? "derived" : "bootstrap");
            HidUartClientStats uartStats = uart.GetStats();
            KeyboardSnapshot keyboardSnapshot = keyboard.Snapshot();
            byte buttons = mouse.GetButtons();
            long uptimeMs = (long)(DateTimeOffset.UtcNow - uartStats.StartedAt).TotalMilliseconds;
            HidInterfaceList? lastList = uart.GetLastInterfaceList();
            object? lastListDto = lastList is null ? null : DeviceInterfaceMapper.MapInterfaceList(lastList);
            string? deviceId = uart.GetDeviceIdHex();
            bool derivedKeyActive = uart.HasDerivedKey();

            return Results.Ok(new
            {
                ok = true,
                uptimeMs,
                uart = uartStats,
                lastInterfaces = lastListDto,
                deviceId,
                derivedKeyActive,
                hmacMode,
                mouse = new { buttons },
                keyboard = keyboardSnapshot,
                config = new
                {
                    mouseReportLen = opt.MouseReportLen,
                    serialPort = opt.SerialPort,
                    serialPortConfigured = appState.SerialConfigured,
                    serialPortResolved = appState.SerialResolved,
                    serialAuto = appState.SerialAutoEnabled,
                    serialMatch = appState.SerialMatch,
                    serialAutoStatus = appState.SerialAutoStatus,
                    injectQueueCapacity = opt.InjectQueueCapacity,
                    injectDropThreshold = opt.InjectDropThreshold,
                    injectTimeoutMs = opt.InjectTimeoutMs,
                    injectRetries = opt.InjectRetries,
                    mouseMoveTimeoutMs = opt.MouseMoveTimeoutMs,
                    mouseMoveDropIfBusy = opt.MouseMoveDropIfBusy,
                    mouseMoveAllowZero = opt.MouseMoveAllowZero,
                    mouseWheelTimeoutMs = opt.MouseWheelTimeoutMs,
                    mouseWheelDropIfBusy = opt.MouseWheelDropIfBusy,
                    mouseWheelAllowZero = opt.MouseWheelAllowZero,
                    keyboardInjectTimeoutMs = opt.KeyboardInjectTimeoutMs,
                    keyboardInjectRetries = opt.KeyboardInjectRetries,
                    masterSecretSet = !string.IsNullOrWhiteSpace(opt.MasterSecret),
                    mouseItfSel = opt.MouseItfSel,
                    keyboardItfSel = opt.KeyboardItfSel,
                    mouseTypeName = opt.MouseTypeName,
                    keyboardTypeName = opt.KeyboardTypeName,
                    devicesAutoMuteLogs = opt.DevicesAutoMuteLogs,
                    devicesAutoRefreshMs = opt.DevicesAutoRefreshMs,
                    devicesIncludeReportDesc = opt.DevicesIncludeReportDesc,
                    bindAll = opt.BindAll,
                    mouseMappingDb = opt.MouseMappingDb,
                    videoSourcesCount = opt.VideoSources.Count,
                    ffmpegPath = opt.FfmpegPath,
                    ffmpegAutoStart = opt.FfmpegAutoStart,
                    ffmpegWatchdogEnabled = opt.FfmpegWatchdogEnabled,
                    ffmpegWatchdogIntervalMs = opt.FfmpegWatchdogIntervalMs,
                    ffmpegRestartDelayMs = opt.FfmpegRestartDelayMs,
                    ffmpegMaxRestarts = opt.FfmpegMaxRestarts,
                    ffmpegRestartWindowMs = opt.FfmpegRestartWindowMs,
                    videoRtspPort = opt.VideoRtspPort,
                    videoSrtBasePort = opt.VideoSrtBasePort,
                    videoSrtLatencyMs = opt.VideoSrtLatencyMs,
                    videoRtmpPort = opt.VideoRtmpPort,
                    videoHlsDir = opt.VideoHlsDir,
                    videoLlHls = opt.VideoLlHls,
                    videoHlsSegmentSeconds = opt.VideoHlsSegmentSeconds,
                    videoHlsListSize = opt.VideoHlsListSize,
                    videoHlsDeleteSegments = opt.VideoHlsDeleteSegments,
                    videoLogDir = opt.VideoLogDir,
                    videoSecondaryCaptureEnabled = opt.VideoSecondaryCaptureEnabled,
                    videoProfilesCount = opt.VideoProfiles.Count,
                    activeVideoProfile = opt.ActiveVideoProfile,
                    pgConnectionStringSet = !string.IsNullOrWhiteSpace(opt.PgConnectionString),
                    migrateSqliteToPg = opt.MigrateSqliteToPg,
                    uartHmacKeySet = !string.IsNullOrWhiteSpace(opt.UartHmacKey),
                    mouseLeftMask = opt.MouseLeftMask,
                    mouseRightMask = opt.MouseRightMask,
                    mouseMiddleMask = opt.MouseMiddleMask,
                    mouseBackMask = opt.MouseBackMask,
                    mouseForwardMask = opt.MouseForwardMask
                }
            });
        });
    }

    private static string GenerateRoomId()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = new byte[10];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(11);
        sb.Append('r');
        foreach (byte b in bytes)
        {
            sb.Append(alphabet[b % alphabet.Length]);
        }
        return sb.ToString();
    }

    private static bool TryNormalizeRoomIdForApi(string room, out string normalized, out string? error)
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
