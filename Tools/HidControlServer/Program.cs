using HidControlServer;
using HidControlServer.Endpoints.Devices;
using HidControlServer.Endpoints.Keyboard;
using HidControlServer.Endpoints.Mouse;
using HidControlServer.Endpoints.System;
using HidControlServer.Endpoints.Uart;
using HidControlServer.Endpoints.Video;
using HidControlServer.Endpoints.Ws;
using HidControl.Application.UseCases;
using HidControlServer.Adapters.Application;
using HidControlServer.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.OpenApi.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

// Parse CLI/config options and resolve effective serial port settings.
var options = Options.Parse(args);
ServerEventLog.Configure(options.ServerEventLogMaxEntries);
ServerEventLog.Log("server", "startup", new { options.ServerEventLogMaxEntries });
string serialConfigured = options.SerialPort;
bool serialAutoEnabled = options.SerialAuto || string.Equals(serialConfigured, "auto", StringComparison.OrdinalIgnoreCase);
string? serialMatch = options.SerialMatch;
string serialAutoStatus = "disabled";
string resolvedSerial = SerialPortService.ResolveSerialPort(serialConfigured, serialAutoEnabled, serialMatch, out serialAutoStatus);
if (!string.IsNullOrWhiteSpace(serialMatch) &&
    (serialMatch.StartsWith("device:", StringComparison.OrdinalIgnoreCase) ||
     serialMatch.StartsWith("deviceid:", StringComparison.OrdinalIgnoreCase)))
{
    if (SerialPortService.TryResolveSerialByDeviceId(serialMatch, options.Baud, DeviceKeyService.GetBootstrapKey(options), options.InjectQueueCapacity, options.InjectDropThreshold, out string? probed, out string probeStatus) &&
        !string.IsNullOrWhiteSpace(probed))
    {
        resolvedSerial = probed;
        serialAutoStatus = probeStatus;
    }
    else
    {
        serialAutoStatus = probeStatus;
    }
}
if (!string.IsNullOrWhiteSpace(resolvedSerial) &&
    !string.Equals(resolvedSerial, options.SerialPort, StringComparison.OrdinalIgnoreCase))
{
    options = options with { SerialPort = resolvedSerial };
}
string serialResolved = options.SerialPort;
// Initialize shared runtime state.
var appState = new AppState
{
    SerialConfigured = serialConfigured,
    SerialResolved = serialResolved,
    SerialAutoEnabled = serialAutoEnabled,
    SerialAutoStatus = serialAutoStatus,
    SerialMatch = serialMatch,
    ConfigPath = Options.ResolveConfigPath(args)
};

// Configure ASP.NET Core host and DI container.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(VideoUrlService.BuildBindUrls(options));
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Core singletons: shared state or long-lived infrastructure.
// Scoped lifetimes are avoided because most services coordinate shared hardware state.
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(appState);
builder.Services.AddSingleton(new HidUartClient(options.SerialPort, options.Baud, DeviceKeyService.GetBootstrapKey(options), options.InjectQueueCapacity, options.InjectDropThreshold));
builder.Services.AddSingleton<KeyboardState>();
builder.Services.AddSingleton<MouseState>();
builder.Services.AddSingleton(new MouseMappingStore(options.PgConnectionString));
builder.Services.AddSingleton(new KeyboardMappingStore(options.PgConnectionString));
builder.Services.AddSingleton(new VideoSourceStore(options.VideoSources));
builder.Services.AddSingleton(new VideoProfileStore(options.VideoProfiles, options.ActiveVideoProfile));
builder.Services.AddSingleton<HidControl.UseCases.Video.IVideoRuntime, HidControlServer.Adapters.Video.VideoRuntimeAdapter>();
builder.Services.AddSingleton<HidControl.UseCases.Video.VideoRuntimeService>();
builder.Services.AddSingleton<HidControl.UseCases.Video.IVideoRuntimeState, HidControlServer.Adapters.Video.VideoRuntimeStateAdapter>();
builder.Services.AddSingleton<HidControl.UseCases.Video.IVideoFfmpegController, HidControlServer.Adapters.Video.VideoFfmpegControllerAdapter>();
builder.Services.AddSingleton<HidControl.UseCases.Video.VideoFfmpegService>();
builder.Services.AddSingleton<HidControl.UseCases.Video.IVideoWatchdogEvents, ServerWatchdogEvents>();
builder.Services.AddSingleton<HidControl.UseCases.Video.VideoWatchdogService>();
builder.Services.AddSingleton<HidControl.UseCases.Video.IVideoOutputStateStore, HidControlServer.Adapters.Video.VideoOutputStateAdapter>();
builder.Services.AddSingleton<HidControl.UseCases.Video.VideoOutputService>();
builder.Services.AddSingleton<HidControl.UseCases.Video.IVideoConfigStore, HidControlServer.Adapters.Video.VideoConfigStoreAdapter>();
builder.Services.AddSingleton<HidControl.UseCases.Video.VideoConfigService>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoProfileStore, VideoProfileStoreAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoConfigSaver, VideoConfigSaverAdapter>();
builder.Services.AddSingleton<GetVideoSourcesUseCase>();
builder.Services.AddSingleton<SetVideoSourcesUseCase>();
builder.Services.AddSingleton<UpsertVideoSourceUseCase>();
builder.Services.AddSingleton<GetVideoProfilesUseCase>();
builder.Services.AddSingleton<SetVideoProfilesUseCase>();
builder.Services.AddSingleton<SetActiveVideoProfileUseCase>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoSourceStore, VideoSourceStoreAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoRuntimeControl, VideoRuntimeControlAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoOutputStateProvider, VideoOutputStateProviderAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoOutputApplier, VideoOutputApplierAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoFfmpegStarter, VideoFfmpegStarterAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoOrphanKiller, VideoOrphanKillerAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoProcessTracker, VideoProcessTrackerAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoFfmpegOptions, VideoFfmpegOptionsAdapter>();
builder.Services.AddSingleton<ApplyVideoOutputUseCase>();
builder.Services.AddSingleton<GetVideoOutputStatusUseCase>();
builder.Services.AddSingleton<StartFfmpegUseCase>();
builder.Services.AddSingleton<StopFfmpegUseCase>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoDiagProvider, VideoDiagProviderAdapter>();
builder.Services.AddSingleton<GetVideoDiagUseCase>();

// Keyboard use cases (Step 5): keep UART implementation in server adapters.
builder.Services.AddSingleton<HidControl.Application.Abstractions.IKeyboardControl, KeyboardControlAdapter>();
builder.Services.AddSingleton<KeyboardPressUseCase>();
builder.Services.AddSingleton<KeyboardDownUseCase>();
builder.Services.AddSingleton<KeyboardUpUseCase>();
builder.Services.AddSingleton<KeyboardTextUseCase>();
builder.Services.AddSingleton<KeyboardShortcutUseCase>();
builder.Services.AddSingleton<KeyboardReportUseCase>();
builder.Services.AddSingleton<KeyboardResetUseCase>();

// Mouse use cases (Step 5): endpoints call application use cases; UART implementation stays in server adapters.
builder.Services.AddSingleton<HidControl.Application.Abstractions.IMouseControl, MouseControlAdapter>();
builder.Services.AddSingleton<MouseMoveUseCase>();
builder.Services.AddSingleton<MouseWheelUseCase>();
builder.Services.AddSingleton<MouseButtonUseCase>();
builder.Services.AddSingleton<MouseButtonsMaskUseCase>();

builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "HidControl Server", Version = "v1" });
});

// Build the web application pipeline.
var app = builder.Build();

// Developer UX endpoints.
app.UseSwagger();
app.UseSwaggerUI();
app.UseWebSockets();

// Graceful shutdown cleanup for UART and ffmpeg processes.
app.Lifetime.ApplicationStopping.Register(() =>
{
    var client = app.Services.GetRequiredService<HidUartClient>();
    client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    foreach (var kvp in appState.FfmpegProcesses)
    {
        try { kvp.Value.Kill(true); } catch { }
    }
    appState.FfmpegProcesses.Clear();
});

// Optional token auth middleware for control/status/ws routes.
if (!string.IsNullOrWhiteSpace(options.Token))
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/control", StringComparison.OrdinalIgnoreCase) ||
            ctx.Request.Path.StartsWithSegments("/status", StringComparison.OrdinalIgnoreCase) ||
            ctx.Request.Path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase))
        {
            string? wsToken = null;
            if (ctx.Request.Headers.TryGetValue("X-HID-Token", out var headerToken))
            {
                wsToken = headerToken.ToString();
            }
            else if (ctx.Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                string value = authHeader.ToString();
                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    wsToken = value.Substring("Bearer ".Length).Trim();
                }
            }
            else if (ctx.Request.Query.TryGetValue("access_token", out var queryToken))
            {
                wsToken = queryToken.ToString();
            }

            if (wsToken != options.Token)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
            return;
        }

        if (!ctx.Request.Headers.TryGetValue("X-HID-Token", out var token) || token != options.Token)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
    });
}

// Ensure device key is initialized for sensitive endpoints when configured.
if (!string.IsNullOrWhiteSpace(options.MasterSecret))
{
    app.Use(async (ctx, next) =>
    {
        string path = ctx.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/devices", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/mouse", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/keyboard", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/raw", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/loglevel", StringComparison.OrdinalIgnoreCase))
        {
            var uart = ctx.RequestServices.GetRequiredService<HidUartClient>();
            await DeviceKeyService.EnsureDeviceKeyAsync(options, uart, ctx.RequestAborted);
        }
        await next();
    });
}

// API endpoints.
app.MapVideoEndpoints();
app.MapDevicesEndpoints();
app.MapMouseEndpoints();
app.MapKeyboardEndpoints();
app.MapUartEndpoints();
app.MapSystemEndpoints();
app.MapHidWsEndpoints();


// Background startup tasks (device key, migrations, auto-refresh, ffmpeg watchdog).
app.Lifetime.ApplicationStarted.Register(() =>
{
    ServerStartupService.StartBackgroundTasks(app, appState);
});

app.MapRazorPages();

app.Run();
