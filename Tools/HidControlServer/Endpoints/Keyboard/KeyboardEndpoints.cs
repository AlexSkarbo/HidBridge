using HidControlServer;
using HidControlServer.Services;
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

        group.MapPost("/press", async (KeyboardPressRequest req, Options opt, HidUartClient uart, KeyboardMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(req.ItfSel, opt.KeyboardTypeName, opt.KeyboardItfSel, uart, ct);
            if (itfSel == 0xFE) return Results.BadRequest(new { ok = false, error = "keyboard_itf_unresolved" });
            await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ct);
            byte usage = InputReportBuilder.ResolveKeyboardUsage(uart, mappingStore, itfSel, req.Usage);
            byte modifiers = req.Modifiers ?? 0;
            if (InputReportBuilder.IsModifierUsage(usage))
            {
                byte bit = InputReportBuilder.ModifierBit(usage);
                byte[] downReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers | bit), Array.Empty<byte>());
                byte[] upReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers & ~bit), Array.Empty<byte>());
                try
                {
                    await uart.SendInjectReportAsync(itfSel, downReportMod, opt.KeyboardInjectTimeoutMs, 0, false, ct);
                    await uart.SendInjectReportAsync(itfSel, upReportMod, opt.KeyboardInjectTimeoutMs, 0, false, ct);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { ok = false, error = ex.Message });
                }
                return Results.Ok(new { ok = true });
            }
            byte[] downReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, modifiers, new[] { usage });
            byte[] upReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, 0, Array.Empty<byte>());
            try
            {
                await uart.SendInjectReportAsync(itfSel, downReport, opt.KeyboardInjectTimeoutMs, 0, false, ct);
                await uart.SendInjectReportAsync(itfSel, upReport, opt.KeyboardInjectTimeoutMs, 0, false, ct);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/down", async (KeyboardPressRequest req, Options opt, KeyboardState state, HidUartClient uart, KeyboardMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(req.ItfSel, opt.KeyboardTypeName, opt.KeyboardItfSel, uart, ct);
            if (itfSel == 0xFE) return Results.BadRequest(new { ok = false, error = "keyboard_itf_unresolved" });
            byte usage = InputReportBuilder.ResolveKeyboardUsage(uart, mappingStore, itfSel, req.Usage);
            if (InputReportBuilder.IsModifierUsage(usage))
            {
                byte bit = InputReportBuilder.ModifierBit(usage);
                state.ModifierDown(bit, req.Modifiers);
            }
            else
            {
                state.KeyDown(usage, req.Modifiers);
            }
            KeyboardSnapshot snap = state.Snapshot();
            await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ct);
            byte[] report = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, snap.Modifiers, snap.Keys);
            try
            {
                await uart.SendInjectReportAsync(itfSel, report, opt.KeyboardInjectTimeoutMs, 0, false, ct);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/up", async (KeyboardPressRequest req, Options opt, KeyboardState state, HidUartClient uart, KeyboardMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(req.ItfSel, opt.KeyboardTypeName, opt.KeyboardItfSel, uart, ct);
            if (itfSel == 0xFE) return Results.BadRequest(new { ok = false, error = "keyboard_itf_unresolved" });
            byte usage = InputReportBuilder.ResolveKeyboardUsage(uart, mappingStore, itfSel, req.Usage);
            if (InputReportBuilder.IsModifierUsage(usage))
            {
                byte bit = InputReportBuilder.ModifierBit(usage);
                state.ModifierUp(bit, req.Modifiers);
            }
            else
            {
                state.KeyUp(usage, req.Modifiers);
            }
            KeyboardSnapshot snap = state.Snapshot();
            await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ct);
            byte[] report = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, snap.Modifiers, snap.Keys);
            try
            {
                await uart.SendInjectReportAsync(itfSel, report, opt.KeyboardInjectTimeoutMs, 0, false, ct);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            return Results.Ok(new { ok = true });
        });

        static async Task<IResult> HandleType(KeyboardTypeRequest req, Options opt, HidUartClient uart, KeyboardMappingStore mappingStore, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(req.Text))
            {
                return Results.Ok(new { ok = true });
            }

            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(req.ItfSel, opt.KeyboardTypeName, opt.KeyboardItfSel, uart, ct);
            if (itfSel == 0xFE) return Results.BadRequest(new { ok = false, error = "keyboard_itf_unresolved" });
            await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ct);
            foreach (char ch in req.Text)
            {
                if (!HidReports.TryMapAsciiToHidKey(ch, out byte modifiers, out byte usage))
                {
                    return Results.BadRequest(new { ok = false, error = $"Unsupported char: U+{(int)ch:X4}" });
                }

                byte mappedUsage = InputReportBuilder.ResolveKeyboardUsage(uart, mappingStore, itfSel, usage);
                if (InputReportBuilder.IsModifierUsage(mappedUsage))
                {
                    byte bit = InputReportBuilder.ModifierBit(mappedUsage);
                    byte[] downReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers | bit), Array.Empty<byte>());
                    byte[] upReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers & ~bit), Array.Empty<byte>());
                    try
                    {
                        await uart.SendInjectReportAsync(itfSel, downReportMod, opt.KeyboardInjectTimeoutMs, 0, false, ct);
                        await uart.SendInjectReportAsync(itfSel, upReportMod, opt.KeyboardInjectTimeoutMs, 0, false, ct);
                    }
                    catch (Exception ex)
                    {
                        return Results.BadRequest(new { ok = false, error = ex.Message });
                    }
                    continue;
                }
                byte[] downReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, modifiers, new[] { mappedUsage });
                byte[] upReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, 0, Array.Empty<byte>());
                try
                {
                    await uart.SendInjectReportAsync(itfSel, downReport, opt.KeyboardInjectTimeoutMs, 0, false, ct);
                    await uart.SendInjectReportAsync(itfSel, upReport, opt.KeyboardInjectTimeoutMs, 0, false, ct);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { ok = false, error = ex.Message });
                }
            }

            return Results.Ok(new { ok = true });
        }

        group.MapPost("/type", HandleType);
        group.MapPost("/text", HandleType);

        group.MapPost("/report", async (KeyboardReportRequest req, Options opt, HidUartClient uart, KeyboardMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(req.ItfSel, opt.KeyboardTypeName, opt.KeyboardItfSel, uart, ct);
            if (itfSel == 0xFE) return Results.BadRequest(new { ok = false, error = "keyboard_itf_unresolved" });
            await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ct);
            byte modifiers = req.Modifiers ?? 0;
            var keys = req.Keys ?? Array.Empty<int>();
            if (keys.Length > 6) return Results.BadRequest(new { ok = false, error = "keys max length is 6" });
            var keyBytes = new List<byte>(keys.Length);
            foreach (int key in keys)
            {
                if (key < 0 || key > 255)
                {
                    return Results.BadRequest(new { ok = false, error = "keys must be 0..255" });
                }
                keyBytes.Add((byte)key);
            }

            bool applyMapping = req.ApplyMapping ?? true;
            var finalKeys = new List<byte>(keyBytes.Count);
            if (applyMapping)
            {
                modifiers = InputReportBuilder.ApplyModifierMapping(uart, mappingStore, itfSel, modifiers, finalKeys);
                foreach (byte key in keyBytes)
                {
                    byte mapped = InputReportBuilder.ResolveKeyboardUsage(uart, mappingStore, itfSel, key);
                    if (InputReportBuilder.IsModifierUsage(mapped))
                    {
                        modifiers = (byte)(modifiers | InputReportBuilder.ModifierBit(mapped));
                        continue;
                    }
                    if (finalKeys.Contains(mapped)) continue;
                    if (finalKeys.Count >= 6) break;
                    finalKeys.Add(mapped);
                }
            }
            else
            {
                foreach (byte key in keyBytes)
                {
                    if (finalKeys.Contains(key)) continue;
                    if (finalKeys.Count >= 6) break;
                    finalKeys.Add(key);
                }
            }

            byte[] report = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, modifiers, finalKeys);
            try
            {
                await uart.SendInjectReportAsync(itfSel, report, opt.KeyboardInjectTimeoutMs, 0, false, ct);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/reset", async (KeyboardResetRequest req, Options opt, KeyboardState state, HidUartClient uart, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(req.ItfSel, opt.KeyboardTypeName, opt.KeyboardItfSel, uart, ct);
            if (itfSel == 0xFE) return Results.BadRequest(new { ok = false, error = "keyboard_itf_unresolved" });
            state.Clear();
            await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ct);
            byte[] report = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, 0, Array.Empty<byte>());
            try
            {
                await uart.SendInjectReportAsync(itfSel, report, opt.KeyboardInjectTimeoutMs, 0, false, ct);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
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
