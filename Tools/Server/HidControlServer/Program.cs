using HidControl.Application.UseCases;
using HidControlServer;
using HidControlServer.Adapters.Application;
using HidControlServer.Endpoints.Devices;
using HidControlServer.Endpoints.Keyboard;
using HidControlServer.Endpoints.Mouse;
using HidControlServer.Endpoints.Uart;
using HidControlServer.Endpoints.Video;
using HidControlServer.Endpoints.Ws;
using HidControlServer.Services;
using Microsoft.AspNetCore.Http.Json;
using Scalar.AspNetCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using HidControlServer.Endpoints.System;
using Microsoft.OpenApi;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

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

// Mutable runtime-managed state store (profiles, room-profile bindings).
string hidStateStorePath = Path.Combine(Directory.GetCurrentDirectory(), "hidcontrol.state.json");
var hidStateStore = new JsonHidStateStore(hidStateStorePath);
bool hidStateExists = File.Exists(hidStateStorePath);
if (!hidStateExists)
{
    // First run bootstrap only: seed mutable state from config, then state-store becomes authoritative.
    hidStateStore.SaveVideoProfiles(options.VideoProfiles, options.ActiveVideoProfile);
    hidStateStore.SaveRoomProfileBindings(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
}
var hidStateSnapshot = hidStateStore.Load();
var bootProfiles = hidStateSnapshot.VideoProfiles;
string bootActiveProfile = string.IsNullOrWhiteSpace(hidStateSnapshot.ActiveVideoProfile)
    ? "auto"
    : hidStateSnapshot.ActiveVideoProfile!;

// Configure ASP.NET Core host and DI container.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(VideoUrlService.BuildBindUrls(options));
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    o.SingleLine = true;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("HidControl.Infrastructure.Services.WebRtcRoomsService", LogLevel.Information);
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Core singletons: shared state or long-lived infrastructure.
// Scoped lifetimes are avoided because most services coordinate shared hardware state.
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(appState);
builder.Services.AddSingleton<HidControl.Application.Abstractions.IHidStateStore>(hidStateStore);
builder.Services.AddSingleton(new HidUartClient(options.SerialPort, options.Baud, DeviceKeyService.GetBootstrapKey(options), options.InjectQueueCapacity, options.InjectDropThreshold));
builder.Services.AddSingleton<KeyboardState>();
builder.Services.AddSingleton<MouseState>();
builder.Services.AddSingleton(new MouseMappingStore(options.PgConnectionString));
builder.Services.AddSingleton(new KeyboardMappingStore(options.PgConnectionString));
builder.Services.AddSingleton(new VideoSourceStore(options.VideoSources));
builder.Services.AddSingleton(new VideoProfileStore(bootProfiles, bootActiveProfile));
builder.Services.AddSingleton<HidControl.Application.Abstractions.IWebRtcSignalingService, HidControl.Infrastructure.Services.WebRtcSignalingService>();
builder.Services.AddSingleton<WebRtcControlPeerSupervisor>();
builder.Services.AddSingleton<WebRtcVideoPeerSupervisor>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IWebRtcBackend, WebRtcBackendAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IWebRtcRoomIdService, HidControl.Infrastructure.Services.WebRtcRoomIdService>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IWebRtcRoomsService, HidControl.Infrastructure.Services.WebRtcRoomsService>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IWebRtcIceService, HidControl.Infrastructure.Services.WebRtcIceService>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IWebRtcConfigService, HidControl.Infrastructure.Services.WebRtcConfigService>();
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
builder.Services.AddSingleton<GetVideoStreamsUseCase>();
builder.Services.AddSingleton<StopVideoCaptureUseCase>();
builder.Services.AddSingleton<GetFfmpegStatusUseCase>();
builder.Services.AddSingleton<GetVideoModesUseCase>();
builder.Services.AddSingleton<GetVideoStreamsConfigUseCase>();
builder.Services.AddSingleton<TestVideoCaptureUseCase>();
builder.Services.AddSingleton<GetVideoProfilesUseCase>();
builder.Services.AddSingleton<SetVideoProfilesUseCase>();
builder.Services.AddSingleton<SetActiveVideoProfileUseCase>();
builder.Services.AddSingleton<UpsertVideoProfileUseCase>();
builder.Services.AddSingleton<DeleteVideoProfileUseCase>();
builder.Services.AddSingleton<CloneVideoProfileUseCase>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoSourceStore, VideoSourceStoreAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoRuntimeControl, VideoRuntimeControlAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoOutputStateProvider, VideoOutputStateProviderAdapter>();

// WebRTC use-cases.
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.CreateWebRtcVideoRoomUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.RestartWebRtcVideoRoomUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.DeleteWebRtcVideoRoomUseCase>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoOutputApplier, VideoOutputApplierAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoFfmpegStarter, VideoFfmpegStarterAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoOrphanKiller, VideoOrphanKillerAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoProcessTracker, VideoProcessTrackerAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoFfmpegOptions, VideoFfmpegOptionsAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoFfmpegStatusProvider, VideoFfmpegStatusProviderAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoOptionsProvider, VideoOptionsProviderAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoUrlBuilder, VideoUrlBuilderAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoInputBuilder, VideoInputBuilderAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoModesProvider, VideoModesProviderAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoStreamArgsBuilder, VideoStreamArgsBuilderAdapter>();
builder.Services.AddSingleton<HidControl.Application.Abstractions.IVideoTestCaptureProbe, VideoTestCaptureProbeAdapter>();
builder.Services.AddSingleton<ApplyVideoOutputUseCase>();
builder.Services.AddSingleton<GetVideoOutputStatusUseCase>();

// WebRTC control-plane use-cases.
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.GetWebRtcClientConfigUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.GetWebRtcIceConfigUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.ListWebRtcRoomsUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.CreateWebRtcRoomUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.DeleteWebRtcRoomUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.JoinWebRtcSignalingUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.ValidateWebRtcSignalUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.LeaveWebRtcSignalingUseCase>();
builder.Services.AddSingleton<HidControl.Application.UseCases.WebRtc.HandleWebRtcSignalingMessageUseCase>();
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
builder.Services.AddOpenApi(openApiOptions =>
{
    openApiOptions.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Version = "0.9.9";
        document.Info.Title = "HidBridge Server API";
        document.Info.Description = "This API for the Hidden Bridge server for HID and remote KVM-style control.";
        document.Info.Contact = new OpenApiContact
        {
            Name = "Alexander Skarbo",
            Email = "alexandr.skarbo@gmail.com",
            Url = new Uri("https://github.com/AlexSkarbo")
        };
        document.Info.License = new OpenApiLicense
        {
            Name = "MIT License",
            Url = new Uri("https://opensource.org/licenses/MIT")
        };
        return Task.CompletedTask;
    });
    openApiOptions.AddScalarTransformers();
});

//builder.Services.AddSwaggerGen(c =>
//{
//    c.SwaggerDoc("v1", new OpenApiInfo { Title = "HidControl Server", Version = "v1" });
//});

// Build the web application pipeline.
var app = builder.Build();

// Developer UX endpoints.
//app.UseSwagger();
//app.UseSwaggerUI();
// Accessible at /scalar (default route)
app.UseWebSockets();

// Graceful shutdown cleanup for UART and ffmpeg processes.
app.Lifetime.ApplicationStopping.Register(() =>
{
    var client = app.Services.GetRequiredService<HidUartClient>();
    client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    try
    {
        app.Services.GetRequiredService<WebRtcControlPeerSupervisor>().Stop();
    }
    catch
    {
        // ignored
    }

    try
    {
        app.Services.GetRequiredService<WebRtcVideoPeerSupervisor>().Stop();
    }
    catch
    {
        // ignored
    }

    foreach (var kvp in appState.FfmpegProcesses)
    {
        try { kvp.Value.Kill(true); }
        catch
        {
            // ignored
        }
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
app.MapWebRtcWsEndpoints();


// Background startup tasks (device key, migrations, auto-refresh, ffmpeg watchdog).
app.Lifetime.ApplicationStarted.Register(() =>
{
    ServerStartupService.StartBackgroundTasks(app, appState);
    TryOpenStartupBrowser(app, options);
});

app.MapRazorPages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();//scalarOptions => scalarOptions.AddDocument("v1", "HidBridge Server API"));
}

app.Run();

static void TryOpenStartupBrowser(WebApplication app, Options options)
{
    try
    {
        if (!TryResolveStartupBrowserPath(out string? configuredPath))
        {
            return;
        }

        string target = BuildStartupBrowserUrl(app, options, configuredPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
        ServerEventLog.Log("server", "browser_opened", new { target });
    }
    catch (Exception ex)
    {
        ServerEventLog.Log("server", "browser_open_failed", new { error = ex.Message });
    }
}

static bool TryResolveStartupBrowserPath(out string? path)
{
    path = null;

    string? envEnabledRaw = Environment.GetEnvironmentVariable("HIDBRIDGE_OPEN_BROWSER");
    if (!string.IsNullOrWhiteSpace(envEnabledRaw) && bool.TryParse(envEnabledRaw, out bool envEnabled))
    {
        if (!envEnabled)
        {
            return false;
        }
        path = Environment.GetEnvironmentVariable("HIDBRIDGE_OPEN_BROWSER_URL");
        return true;
    }

    string launchSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "Properties", "launchSettings.json");
    if (!File.Exists(launchSettingsPath))
    {
        return false;
    }

    using var doc = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
    if (!doc.RootElement.TryGetProperty("profiles", out JsonElement profiles) || profiles.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    string? profileName = Environment.GetEnvironmentVariable("DOTNET_LAUNCH_PROFILE");
    JsonElement profile;
    if (!string.IsNullOrWhiteSpace(profileName) && profiles.TryGetProperty(profileName, out JsonElement namedProfile))
    {
        profile = namedProfile;
    }
    else
    {
        JsonElement? first = null;
        foreach (JsonProperty p in profiles.EnumerateObject())
        {
            first = p.Value;
            break;
        }
        if (first is null)
        {
            return false;
        }
        profile = first.Value;
    }

    bool launchBrowser = profile.TryGetProperty("launchBrowser", out JsonElement launchBrowserEl) &&
        launchBrowserEl.ValueKind == JsonValueKind.True;
    if (!launchBrowser)
    {
        return false;
    }

    if (profile.TryGetProperty("launchUrl", out JsonElement launchUrlEl) && launchUrlEl.ValueKind == JsonValueKind.String)
    {
        path = launchUrlEl.GetString();
    }

    return true;
}

static string BuildStartupBrowserUrl(WebApplication app, Options options, string? configuredPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath) &&
        Uri.TryCreate(configuredPath, UriKind.Absolute, out Uri? absolute))
    {
        return absolute.ToString();
    }

    string baseUrl = ResolveBrowserBaseUrl(app, options);
    if (string.IsNullOrWhiteSpace(configuredPath))
    {
        return baseUrl;
    }

    if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
    {
        baseUrl += "/";
    }

    return new Uri(new Uri(baseUrl), configuredPath.TrimStart('/')).ToString();
}

static string ResolveBrowserBaseUrl(WebApplication app, Options options)
{
    string? candidate = app.Urls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ??
        app.Urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ??
        options.Url;

    if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
    {
        return options.Url;
    }

    string host = uri.Host;
    if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "[::]", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "+", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "*", StringComparison.OrdinalIgnoreCase))
    {
        host = "127.0.0.1";
    }

    var builder = new UriBuilder(uri)
    {
        Host = host
    };
    return builder.Uri.GetLeftPart(UriPartial.Authority);
}
