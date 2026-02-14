using System.Reflection;
using HidControlServer.Endpoints.Sys;
using HidControlServer.Services;

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

        // WebRTC status/control endpoints are registered in a dedicated mapper to keep this class thin.
        app.MapWebRtcStatusEndpoints();

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
