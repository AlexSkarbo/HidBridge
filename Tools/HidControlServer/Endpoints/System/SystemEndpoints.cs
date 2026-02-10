using System.Reflection;
using HidControlServer;
using HidControlServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HidControlServer.Endpoints.Sys;

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
        app.MapGet("/status/webrtc/ice", async (HidControl.Application.UseCases.WebRtc.GetWebRtcIceConfigUseCase uc, CancellationToken ct) =>
        {
            var cfg = await uc.Execute(ct);
            var iceServers = cfg.IceServers
                .Select(s => new
                {
                    urls = s.Urls,
                    username = s.Username,
                    credential = s.Credential,
                    credentialType = s.CredentialType
                })
                .ToList();

            return Results.Ok(new { ok = true, ttlSeconds = cfg.TtlSeconds, iceServers });
        });

        // WebRTC client-side timeouts/config for UIs.
        app.MapGet("/status/webrtc/config", async (HidControl.Application.UseCases.WebRtc.GetWebRtcClientConfigUseCase uc, CancellationToken ct) =>
        {
            var cfg = await uc.Execute(ct);
            return Results.Ok(new
            {
                ok = true,
                joinTimeoutMs = cfg.JoinTimeoutMs,
                connectTimeoutMs = cfg.ConnectTimeoutMs,
                roomsCleanupIntervalSeconds = cfg.RoomsCleanupIntervalSeconds,
                roomIdleStopSeconds = cfg.RoomIdleStopSeconds,
                roomsMaxHelpers = cfg.RoomsMaxHelpers
            });
        });

        app.MapGet("/status/webrtc/rooms", async (HidControl.Application.UseCases.WebRtc.ListWebRtcRoomsUseCase uc, CancellationToken ct) =>
        {
            var snap = await uc.Execute(ct);
            var rooms = snap.Rooms.Select(r => new { room = r.Room, peers = r.Peers, hasHelper = r.HasHelper, isControl = r.IsControl }).ToArray();
            return Results.Ok(new { ok = true, rooms });
        });

        app.MapPost("/status/webrtc/rooms", async (HttpRequest req, HidControl.Application.UseCases.WebRtc.CreateWebRtcRoomUseCase uc, CancellationToken ct) =>
        {
            string? roomId = null;
            try
            {
                var raw = await req.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
                if (raw.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (raw.TryGetProperty("room", out var lower) && lower.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        roomId = lower.GetString();
                    }
                    else if (raw.TryGetProperty("Room", out var upper) && upper.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        roomId = upper.GetString();
                    }
                }
            }
            catch { }

            var res = await uc.Execute(roomId, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, started = res.Started, pid = res.Pid, error = res.Error });
        });

        app.MapDelete("/status/webrtc/rooms/{room}", async (string room, HidControl.Application.UseCases.WebRtc.DeleteWebRtcRoomUseCase uc, CancellationToken ct) =>
        {
            var res = await uc.Execute(room, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, error = res.Error });
        });

        // WebRTC "video room" lifecycle (skeleton).
        //
        // Video rooms are a separate namespace (recommended) so we can later attach WebRTC media tracks
        // without mixing them with control-plane sessions. Current naming convention:
        // - Default room: "video"
        // - Generated rooms: "hb-v-<deviceIdShort>-<rand>"
        app.MapGet("/status/webrtc/video/rooms", async (HidControl.Application.UseCases.WebRtc.ListWebRtcRoomsUseCase uc, CancellationToken ct) =>
        {
            var snap = await uc.Execute(ct);
            var rooms = snap.Rooms
                .Where(r => string.Equals(r.Room, "video", StringComparison.OrdinalIgnoreCase) || r.Room.StartsWith("hb-v-", StringComparison.OrdinalIgnoreCase) || r.Room.StartsWith("video-", StringComparison.OrdinalIgnoreCase))
                .Select(r => new { room = r.Room, peers = r.Peers, hasHelper = r.HasHelper })
                .ToArray();
            return Results.Ok(new { ok = true, rooms });
        });

        app.MapPost("/status/webrtc/video/rooms", async (HttpRequest req, HidControl.Application.UseCases.WebRtc.CreateWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            string? roomId = null;
            try
            {
                var raw = await req.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
                if (raw.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (raw.TryGetProperty("room", out var lower) && lower.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        roomId = lower.GetString();
                    }
                    else if (raw.TryGetProperty("Room", out var upper) && upper.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        roomId = upper.GetString();
                    }
                }
            }
            catch { }

            var res = await uc.Execute(roomId, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, started = res.Started, pid = res.Pid, error = res.Error });
        });

        app.MapDelete("/status/webrtc/video/rooms/{room}", async (string room, HidControl.Application.UseCases.WebRtc.DeleteWebRtcVideoRoomUseCase uc, CancellationToken ct) =>
        {
            var res = await uc.Execute(room, ct);
            return Results.Ok(new { ok = res.Ok, room = res.Room, stopped = res.Stopped, error = res.Error });
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

    // Room ID generation and validation live in Infrastructure (WebRtcRoomsService) so API endpoints remain thin.
}
