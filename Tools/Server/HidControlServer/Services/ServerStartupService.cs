using HidControl.UseCases.Video;

namespace HidControlServer.Services;

/// <summary>
/// Wires background startup tasks for the server.
/// </summary>
internal static class ServerStartupService
{
    /// <summary>
    /// Starts background initialization tasks.
    /// </summary>
    /// <param name="app">Web application.</param>
    /// <param name="appState">Application state.</param>
    public static void StartBackgroundTasks(WebApplication app, AppState appState)
    {
        var opt = app.Services.GetRequiredService<Options>();
        var uart = app.Services.GetRequiredService<HidUartClient>();
        var videoSources = app.Services.GetRequiredService<VideoSourceStore>();
        var outputService = app.Services.GetRequiredService<VideoOutputService>();
        var ffmpegService = app.Services.GetRequiredService<VideoFfmpegService>();
        var watchdogService = app.Services.GetRequiredService<VideoWatchdogService>();
        var runtimeState = app.Services.GetRequiredService<IVideoRuntimeState>();

        _ = Task.Run(() => DeviceKeyService.InitializeDeviceKeyAsync(opt, uart, app.Lifetime.ApplicationStopping));
        if (opt.MigrateSqliteToPg)
        {
            var mouseStore = app.Services.GetRequiredService<MouseMappingStore>();
            var keyboardStore = app.Services.GetRequiredService<KeyboardMappingStore>();
            _ = Task.Run(() => SqliteMappingMigrator.MigrateAsync(opt.MouseMappingDb, mouseStore, keyboardStore, app.Lifetime.ApplicationStopping));
        }
        if (opt.DevicesAutoRefreshMs > 0)
        {
            _ = Task.Run(() => DeviceDiagnostics.AutoRefreshDevicesLoop(opt, uart, app.Lifetime.ApplicationStopping));
        }
        DeviceCacheService.LoadDevicesCache(opt, appState);
        VideoStartupService.InitializeDefaultVideoOutput(opt, videoSources, appState, outputService);

        // Optional WebRTC control helper auto-start (runs next to the server).
        app.Services.GetRequiredService<WebRtcControlPeerSupervisor>().StartIfEnabled();
        // Optional WebRTC video helper auto-start (runs next to the server).
        app.Services.GetRequiredService<WebRtcVideoPeerSupervisor>().StartIfEnabled();

        if (opt.FfmpegAutoStart)
        {
            _ = Task.Run(() =>
            {
                var sources = videoSources.GetAll().Where(s => s.Enabled && !appState.IsManualStop(s.Id)).ToList();
                var outputState = outputService.Get();
                ffmpegService.StartForSources(sources, outputState, restart: true, force: false);
            });
        }
        if (opt.FfmpegWatchdogEnabled)
        {
            ServerEventLog.Log("watchdog", "enabled", new
            {
                opt.FfmpegWatchdogIntervalMs,
                opt.FfmpegMaxRestarts,
                opt.FfmpegRestartDelayMs,
                opt.FfmpegRestartWindowMs,
                opt.VideoKillOrphanFfmpeg
            });
            _ = Task.Run(async () =>
            {
                var watchdogOptions = new VideoWatchdogOptions(
                    opt.FfmpegMaxRestarts,
                    opt.FfmpegRestartDelayMs,
                    opt.FfmpegRestartWindowMs,
                    opt.VideoKillOrphanFfmpeg,
                    FlvStaleSeconds: 5);
                while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    var outputState = outputService.Get();
                    watchdogService.RunFfmpegWatchdog(watchdogOptions, videoSources, outputState, runtimeState);
                    watchdogService.RunFlvWatchdog(watchdogOptions, videoSources, outputState, runtimeState);
                    try
                    {
                        await Task.Delay(opt.FfmpegWatchdogIntervalMs, app.Lifetime.ApplicationStopping);
                    }
                    catch
                    {
                        break;
                    }
                }
            });
        }
        else
        {
            ServerEventLog.Log("watchdog", "disabled", new { opt.FfmpegWatchdogEnabled });
        }
    }
}
