using HidControlServer;
using HidControlServer.Services;
using HidControl.Application.UseCases;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HidControlServer.Endpoints.Keyboard;

/// <summary>
/// Registers Keyboard endpoints.
/// </summary>
public static class KeyboardEndpoints
{
    /// <summary>
    /// Maps keyboard endpoints.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapKeyboardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/keyboard");

        group.MapPost("/press", async (KeyboardPressRequest req, KeyboardPressUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/down", async (KeyboardPressRequest req, KeyboardDownUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/up", async (KeyboardPressRequest req, KeyboardUpUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }
            return Results.Ok(new { ok = true });
        });

        static async Task<IResult> HandleType(KeyboardTypeRequest req, KeyboardTextUseCase useCase, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(req.Text))
            {
                return Results.Ok(new { ok = true });
            }

            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                if (result.UnsupportedChar is int ch)
                {
                    return Results.BadRequest(new { ok = false, error = $"Unsupported char: U+{ch:X4}" });
                }
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }

            return Results.Ok(new { ok = true });
        }

        group.MapPost("/type", HandleType);
        group.MapPost("/text", HandleType);

        group.MapPost("/shortcut", async (KeyboardShortcutRequest req, KeyboardShortcutUseCase useCase, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Shortcut))
            {
                return Results.BadRequest(new { ok = false, error = "shortcut is required" });
            }

            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }

            return Results.Ok(new
            {
                ok = true,
                itfSel = result.ItfSel,
                shortcut = req.Shortcut,
                modifiers = result.Modifiers,
                keys = result.Keys
            });
        });

        group.MapPost("/report", async (KeyboardReportRequest req, KeyboardReportUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/reset", async (KeyboardResetRequest req, KeyboardResetUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapGet("/state", (KeyboardState state) =>
        {
            KeyboardSnapshot snap = state.Snapshot();
            return Results.Ok(new { ok = true, modifiers = snap.Modifiers, keys = snap.Keys });
        });

        group.MapGet("/layout", async (byte itf, byte? reportId, HidUartClient uart, CancellationToken ct) =>
        {
            byte rid = reportId ?? 0;
            await ReportLayoutService.EnsureReportLayoutAsync(uart, itf, ct, rid);
            ReportLayoutSnapshot? snap = uart.GetReportLayoutCopy(itf);
            if (snap?.Keyboard is null)
            {
                return Results.NotFound(new { ok = false, error = "no keyboard layout" });
            }
            return Results.Ok(new
            {
                ok = true,
                itf,
                reportIdRequested = rid,
                capturedAt = snap.CapturedAt,
                layoutKind = snap.LayoutKind,
                flags = snap.Flags,
                reportId = snap.Keyboard.ReportId,
                reportLen = snap.Keyboard.ReportLen,
                hasReportId = snap.Keyboard.HasReportId
            });
        });

        group.MapGet("/layouts", async (byte itf, int? maxReportId, HidUartClient uart, CancellationToken ct) =>
        {
            int max = maxReportId ?? 16;
            if (max < 0) max = 0;
            if (max > 255) max = 255;
            var list = new List<object>();
            var seen = new HashSet<byte>();
            for (int i = 0; i <= max; i++)
            {
                byte rid = (byte)i;
                ReportLayoutSnapshot? snap;
                try
                {
                    snap = await uart.RequestReportLayoutAsync(itf, rid, 400, ct);
                }
                catch
                {
                    continue;
                }
                if (snap?.Keyboard is null) continue;
                if (!seen.Add(snap.ReportId)) continue;
                list.Add(new
                {
                    reportIdRequested = rid,
                    reportId = snap.Keyboard.ReportId,
                    reportLen = snap.Keyboard.ReportLen,
                    hasReportId = snap.Keyboard.HasReportId,
                    capturedAt = snap.CapturedAt,
                    layoutKind = snap.LayoutKind,
                    flags = snap.Flags
                });
            }
            return Results.Ok(new { ok = true, itf, maxReportId = max, layouts = list });
        });

        group.MapPost("/mapping", (KeyboardMappingRequest req, KeyboardMappingStore store) =>
        {
            if (string.IsNullOrWhiteSpace(req.DeviceId))
            {
                return Results.BadRequest(new { ok = false, error = "deviceId is required" });
            }
            if (string.IsNullOrWhiteSpace(req.ReportDescHash))
            {
                return Results.BadRequest(new { ok = false, error = "reportDescHash is required" });
            }
            var record = new KeyboardMappingRecord(
                req.DeviceId,
                req.Itf,
                req.ReportDescHash,
                req.Mapping,
                DateTimeOffset.UtcNow);
            store.Upsert(record);
            return Results.Ok(new { ok = true });
        });

        group.MapGet("/mapping", (string deviceId, KeyboardMappingStore store) =>
        {
            KeyboardMappingRecord? record = store.Get(deviceId);
            if (record is null)
            {
                return Results.NotFound(new { ok = false, error = "not found" });
            }
            return Results.Ok(new
            {
                ok = true,
                deviceId = record.DeviceId,
                itf = record.Itf,
                reportDescHash = record.ReportDescHash,
                mapping = record.Mapping,
                updatedAt = record.UpdatedAt
            });
        });

        group.MapGet("/mapping/byItf", (byte itf, HidUartClient uart, KeyboardMappingStore store) =>
        {
            string? deviceId = InputReportBuilder.GetDeviceIdForItf(uart, itf);
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return Results.NotFound(new { ok = false, error = "deviceId not available" });
            }
            KeyboardMappingRecord? record = store.Get(deviceId);
            if (record is null)
            {
                return Results.NotFound(new { ok = false, error = "not found" });
            }
            return Results.Ok(new
            {
                ok = true,
                deviceId = record.DeviceId,
                itf = record.Itf,
                reportDescHash = record.ReportDescHash,
                mapping = record.Mapping,
                updatedAt = record.UpdatedAt
            });
        });
    }
}
