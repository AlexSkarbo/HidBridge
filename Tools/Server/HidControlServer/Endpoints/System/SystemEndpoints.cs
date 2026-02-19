using System.Reflection;
using HidControl.Application.Abstractions;
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
        var group = app.MapGroup(string.Empty)
            .WithTags("System");

        group.MapGet("/health", () => Results.Ok(new { ok = true }))
            .WithSummary("Health check")
            .WithDescription("Returns lightweight API availability status.");

        group.MapGet("/version", () =>
        {
            string? repoVersion = DeviceDiagnostics.FindRepoVersion();
            string? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            return Results.Ok(new
            {
                ok = true,
                repoVersion,
                assemblyVersion
            });
        })
            .WithSummary("Server version info")
            .WithDescription("Returns repository and assembly version metadata.");

        group.MapGet("/config", (Options opt) =>
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
                mouseMappingDbDeprecated = true,
                videoSourcesCount = opt.VideoSources.Count,
                ffmpegPath = opt.FfmpegPath,
                ffmpegAutoStart = opt.FfmpegAutoStart,
                ffmpegWatchdogEnabled = opt.FfmpegWatchdogEnabled,
                ffmpegWatchdogIntervalMs = opt.FfmpegWatchdogIntervalMs,
                ffmpegRestartDelayMs = opt.FfmpegRestartDelayMs,
                ffmpegMaxRestarts = opt.FfmpegMaxRestarts,
                ffmpegRestartWindowMs = opt.FfmpegRestartWindowMs,
                serverLogDir = opt.ServerLogDir,
                serverLogRotateMinutes = opt.ServerLogRotateMinutes,
                serverLogRetentionMinutes = opt.ServerLogRetentionMinutes,
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
                videoProfilesInConfigDeprecated = true,
                activeVideoProfileInConfigDeprecated = true,
                pgConnectionStringSet = !string.IsNullOrWhiteSpace(opt.PgConnectionString),
                migrateSqliteToPg = opt.MigrateSqliteToPg,
                migrateSqliteToPgDeprecated = true,
                stateStorePath = Path.Combine(Directory.GetCurrentDirectory(), "hidcontrol.state.json"),
                stateStoreAuthoritativeForMutableState = true,
                authoritativeSources = new
                {
                    videoProfiles = "hidcontrol.state.json",
                    activeVideoProfile = "hidcontrol.state.json",
                    roomProfileBindings = "hidcontrol.state.json",
                    mouseMappings = !string.IsNullOrWhiteSpace(opt.PgConnectionString) ? "postgresql" : "sqlite_legacy"
                },
                deprecations = new
                {
                    mouseMappingDb = new
                    {
                        deprecated = true,
                        replacement = "pgConnectionString",
                        removalTarget = "day4_or_next_release"
                    },
                    migrateSqliteToPg = new
                    {
                        deprecated = true,
                        replacement = "startup_import_from_sqlite_marker",
                        removalTarget = "day4_or_next_release"
                    },
                    videoProfilesInConfig = new
                    {
                        deprecated = true,
                        replacement = "hidcontrol.state.json.videoProfiles",
                        removalTarget = "day4_or_next_release"
                    },
                    activeVideoProfileInConfig = new
                    {
                        deprecated = true,
                        replacement = "hidcontrol.state.json.activeVideoProfile",
                        removalTarget = "day4_or_next_release"
                    }
                },
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
        })
            .WithSummary("Effective server configuration")
            .WithDescription("Returns runtime-effective configuration, deprecation markers and authoritative storage sources.");

        group.MapGet("/logs/last", (int? limit) =>
        {
            var items = ServerEventLog.GetRecent(limit);
            return Results.Ok(new { ok = true, items });
        })
            .WithSummary("Recent server events")
            .WithDescription("Returns the latest server event log entries. Use query parameter 'limit' to cap results.");

        group.MapGet("/status/storage", (Options opt, IHidStateStore stateStore) =>
        {
            HidStateStoreStatus stateStatus = stateStore.GetStatus();
            string sqlitePath = opt.MouseMappingDb;
            string markerPath = $"{sqlitePath}.imported_to_pg.marker.json";
            var snapshot = stateStore.Load();

            return Results.Ok(new
            {
                ok = true,
                runtimeMappingStore = "postgresql",
                pgConnectionStringSet = !string.IsNullOrWhiteSpace(opt.PgConnectionString),
                sqliteLegacyPath = sqlitePath,
                sqliteExists = !string.IsNullOrWhiteSpace(sqlitePath) && File.Exists(sqlitePath),
                sqliteImportMode = opt.MigrateSqliteToPg ? "deprecated" : "disabled",
                sqliteImportMarkerPath = markerPath,
                sqliteImportMarkerExists = File.Exists(markerPath),
                authoritativeSource = new
                {
                    profiles = "hidcontrol.state.json",
                    activeProfile = "hidcontrol.state.json",
                    roomProfileBindings = "hidcontrol.state.json"
                },
                stateStore = new
                {
                    backend = stateStatus.Backend,
                    schemaVersion = stateStatus.SchemaVersion,
                    path = stateStatus.Path,
                    exists = stateStatus.Exists,
                    fileSizeBytes = stateStatus.FileSizeBytes,
                    lastWriteUtc = stateStatus.LastWriteUtc,
                    profilesCount = snapshot.VideoProfiles.Count,
                    activeProfile = snapshot.ActiveVideoProfile,
                    roomBindingsCount = snapshot.RoomProfileBindings.Count
                }
            });
        })
            .WithSummary("Storage status")
            .WithDescription("Returns storage backends, migration markers and mutable-state authoritative source details.");

        // WebRTC status/control endpoints are registered in a dedicated mapper to keep this class thin.
        app.MapWebRtcStatusEndpoints();

        group.MapGet("/serial/ports", () =>
        {
            var list = SerialPortService.ListSerialPortCandidates();
            return Results.Ok(new { ok = true, ports = list });
        })
            .WithSummary("List serial ports")
            .WithDescription("Returns serial COM/tty candidates discovered on the host.");

        group.MapGet("/serial/probe", (Options opt, int? timeoutMs) =>
        {
            int timeout = Math.Clamp(timeoutMs ?? 600, 100, 3000);
            var list = SerialPortService.ProbeSerialDevices(opt.Baud, DeviceKeyService.GetBootstrapKey(opt), opt.InjectQueueCapacity, opt.InjectDropThreshold, timeout);
            return Results.Ok(new { ok = true, timeoutMs = timeout, ports = list });
        })
            .WithSummary("Probe serial HID bridge devices")
            .WithDescription("Performs active serial probing with optional timeoutMs query parameter.");

        group.MapGet("/stats", (Options opt, HidUartClient uart, KeyboardState keyboard, MouseState mouse) =>
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
                    mouseMappingDbDeprecated = true,
                    videoSourcesCount = opt.VideoSources.Count,
                    ffmpegPath = opt.FfmpegPath,
                    ffmpegAutoStart = opt.FfmpegAutoStart,
                    ffmpegWatchdogEnabled = opt.FfmpegWatchdogEnabled,
                    ffmpegWatchdogIntervalMs = opt.FfmpegWatchdogIntervalMs,
                    ffmpegRestartDelayMs = opt.FfmpegRestartDelayMs,
                    ffmpegMaxRestarts = opt.FfmpegMaxRestarts,
                    ffmpegRestartWindowMs = opt.FfmpegRestartWindowMs,
                    serverLogDir = opt.ServerLogDir,
                    serverLogRotateMinutes = opt.ServerLogRotateMinutes,
                    serverLogRetentionMinutes = opt.ServerLogRetentionMinutes,
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
                    videoProfilesInConfigDeprecated = true,
                    activeVideoProfileInConfigDeprecated = true,
                    pgConnectionStringSet = !string.IsNullOrWhiteSpace(opt.PgConnectionString),
                    migrateSqliteToPg = opt.MigrateSqliteToPg,
                    migrateSqliteToPgDeprecated = true,
                    stateStorePath = Path.Combine(Directory.GetCurrentDirectory(), "hidcontrol.state.json"),
                    stateStoreAuthoritativeForMutableState = true,
                    authoritativeSources = new
                    {
                        videoProfiles = "hidcontrol.state.json",
                        activeVideoProfile = "hidcontrol.state.json",
                        roomProfileBindings = "hidcontrol.state.json",
                        mouseMappings = !string.IsNullOrWhiteSpace(opt.PgConnectionString) ? "postgresql" : "sqlite_legacy"
                    },
                    deprecations = new
                    {
                        mouseMappingDb = new
                        {
                            deprecated = true,
                            replacement = "pgConnectionString",
                            removalTarget = "day4_or_next_release"
                        },
                        migrateSqliteToPg = new
                        {
                            deprecated = true,
                            replacement = "startup_import_from_sqlite_marker",
                            removalTarget = "day4_or_next_release"
                        },
                        videoProfilesInConfig = new
                        {
                            deprecated = true,
                            replacement = "hidcontrol.state.json.videoProfiles",
                            removalTarget = "day4_or_next_release"
                        },
                        activeVideoProfileInConfig = new
                        {
                            deprecated = true,
                            replacement = "hidcontrol.state.json.activeVideoProfile",
                            removalTarget = "day4_or_next_release"
                        }
                    },
                    uartHmacKeySet = !string.IsNullOrWhiteSpace(opt.UartHmacKey),
                    mouseLeftMask = opt.MouseLeftMask,
                    mouseRightMask = opt.MouseRightMask,
                    mouseMiddleMask = opt.MouseMiddleMask,
                    mouseBackMask = opt.MouseBackMask,
                    mouseForwardMask = opt.MouseForwardMask
                }
            });
        })
            .WithSummary("Runtime stats")
            .WithDescription("Returns UART, keyboard, mouse, video and WebRTC runtime telemetry snapshot.");
    }

    // Room ID generation and validation live in Infrastructure (WebRtcRoomsService) so API endpoints remain thin.
}
