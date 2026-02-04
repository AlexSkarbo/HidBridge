using HidControlServer;
using HidControlServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HidControlServer.Endpoints.Devices;

/// <summary>
/// Registers Devices endpoints.
/// </summary>
public static class DevicesEndpoints
{
    /// <summary>
    /// Maps devices endpoints.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapDevicesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/devices");

        var appState = app.Services.GetRequiredService<AppState>();

        group.MapGet("/last", (bool? includeReportDesc) =>
        {
            if (appState.CachedDevicesDetailDoc is null)
            {
                return Results.NotFound(new { ok = false, error = "no cached devices" });
            }
            return Results.Ok(new
            {
                ok = true,
                stale = true,
                staleAt = appState.CachedDevicesAt,
                list = appState.CachedDevicesDetailDoc.RootElement
            });
        });

        group.MapGet("", async (int? timeoutMs, bool? includeReportDesc, bool? useBootstrapKey, bool? allowStale, Options opt, HidUartClient uart, MouseMappingStore mouseStore, KeyboardMappingStore keyboardStore, CancellationToken ct) =>
        {
            int timeout = timeoutMs ?? 500;
            bool includeDesc = includeReportDesc ?? opt.DevicesIncludeReportDesc;
            byte prevLevel = uart.GetLogLevel();
            bool muted = false;
            if (opt.DevicesAutoMuteLogs && prevLevel != 0)
            {
                await uart.SetLogLevelAsync(0, ct);
                muted = true;
            }

            try
            {
                bool bootstrap = useBootstrapKey ?? false;
                HidInterfaceList? list = await uart.RequestInterfaceListAsync(timeout, bootstrap, ct);
                bool usedBootstrap = bootstrap;
                if (list is null && !bootstrap)
                {
                    list = await uart.RequestInterfaceListAsync(timeout, true, ct);
                    usedBootstrap = list is not null;
                    if (usedBootstrap) uart.UseBootstrapHmacKey();
                }
                if (list is null)
                {
                    HidInterfaceList? last = uart.GetLastInterfaceList();
                    if (last is not null)
                    {
                        if (includeDesc)
                        {
                            foreach (HidInterfaceInfo itf in last.Interfaces)
                            {
                                try { await uart.RequestReportLayoutAsync(itf.Itf, 0, 400, ct); } catch { }
                                try { await uart.RequestReportLayoutAsync(itf.Itf, 0, 400, ct, true); } catch { }
                                try { await uart.RequestReportDescriptorAsync(itf.Itf, 400, ct); } catch { }
                                try { await uart.RequestReportDescriptorAsync(itf.Itf, 400, ct, true); } catch { }
                            }
                        }
                        var mappedLast = DeviceInterfaceMapper.MapInterfaceListDetailed(last, uart, includeDesc);
                        DeviceCacheService.SaveDevicesCache(opt, mappedLast, appState);
                        return Results.Ok(new
                        {
                            ok = true,
                            stale = true,
                            staleAt = last.CapturedAt,
                            list = mappedLast
                        });
                    }
                    if (allowStale == true && appState.CachedDevicesDetailDoc is not null)
                    {
                        return Results.Ok(new
                        {
                            ok = true,
                            stale = true,
                            staleAt = appState.CachedDevicesAt,
                            list = appState.CachedDevicesDetailDoc.RootElement
                        });
                    }
                    if (allowStale == true)
                    {
                        object fallback = DeviceInterfaceMapper.BuildDevicesFromMappings(mouseStore, keyboardStore);
                        return Results.Ok(new
                        {
                            ok = true,
                            stale = true,
                            staleAt = DateTimeOffset.UtcNow,
                            list = fallback
                        });
                    }
                    return Results.Json(new { ok = false, error = "timeout" }, statusCode: StatusCodes.Status504GatewayTimeout);
                }
                if (includeDesc)
                {
                    foreach (HidInterfaceInfo itf in list.Interfaces)
                    {
                        try { await uart.RequestReportDescriptorAsync(itf.Itf, 400, ct); } catch { }
                        try { await uart.RequestReportLayoutAsync(itf.Itf, 0, 400, ct); } catch { }
                    }
                }
                var mapped = DeviceInterfaceMapper.MapInterfaceListDetailed(list, uart, includeDesc);
                DeviceCacheService.SaveDevicesCache(opt, mapped, appState);
                if (uart.IsBootstrapForced() || usedBootstrap)
                {
                    return Results.Ok(new { ok = true, keyMode = "bootstrap", warning = "derived key failed; using bootstrap", list = mapped });
                }
                return Results.Ok(new { ok = true, list = mapped });
            }
            finally
            {
                if (muted)
                {
                    try { await uart.SetLogLevelAsync(prevLevel, CancellationToken.None); } catch { }
                }
            }
        });
    }
}
