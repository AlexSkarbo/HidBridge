using System.Text.Json;
using HidControl.Application.UseCases;
using HidControlServer;
using HidControlServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HidControlServer.Endpoints.Mouse;

/// <summary>
/// Registers Mouse endpoints.
/// </summary>
public static class MouseEndpoints
{
    /// <summary>
    /// Maps mouse endpoints.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapMouseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/mouse");

        group.MapPost("/move", async (MouseMoveRequest req, MouseMoveUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }
            if (result.Skipped)
            {
                return Results.Ok(new { ok = true, skipped = true });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/button", async (MouseButtonRequest req, MouseButtonUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/wheel", async (MouseWheelRequest req, MouseWheelUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }
            if (result.Skipped)
            {
                return Results.Ok(new { ok = true, skipped = true });
            }
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/buttons", async (MouseButtonsMaskRequest req, MouseButtonsMaskUseCase useCase, CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(req, ct);
            if (!result.Ok)
            {
                return Results.BadRequest(new { ok = false, error = result.Error ?? "failed" });
            }
            return Results.Ok(new { ok = true });
        });

        DateTimeOffset lastButtonsMapCapturedAt = DateTimeOffset.MinValue;
        JsonElement? lastButtonsMapMapping = null;

        group.MapPost("/testButtonsMap", (JsonElement payload) =>
        {
            lastButtonsMapCapturedAt = DateTimeOffset.UtcNow;
            lastButtonsMapMapping = payload.Clone();
            return Results.Ok(new { ok = true });
        });

        group.MapGet("/testButtonsMap", () =>
        {
            return Results.Ok(new
            {
                ok = true,
                lastButtonsMap = new
                {
                    capturedAt = lastButtonsMapCapturedAt,
                    mapping = lastButtonsMapMapping
                }
            });
        });

        group.MapPost("/mapping", (MouseMappingRequest req, MouseMappingStore store) =>
        {
            if (string.IsNullOrWhiteSpace(req.DeviceId))
            {
                return Results.BadRequest(new { ok = false, error = "deviceId is required" });
            }
            if (string.IsNullOrWhiteSpace(req.ReportDescHash))
            {
                return Results.BadRequest(new { ok = false, error = "reportDescHash is required" });
            }
            var record = new MouseMappingRecord(
                req.DeviceId,
                req.Itf,
                req.ReportDescHash,
                req.ButtonsCount,
                req.Mapping,
                DateTimeOffset.UtcNow);
            store.Upsert(record);
            return Results.Ok(new { ok = true });
        });

        group.MapGet("/mapping", (string deviceId, MouseMappingStore store) =>
        {
            MouseMappingRecord? record = store.Get(deviceId);
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
                buttonsCount = record.ButtonsCount,
                mapping = record.Mapping,
                updatedAt = record.UpdatedAt
            });
        });

        group.MapGet("/mapping/byItf", (byte itf, HidUartClient uart, MouseMappingStore store) =>
        {
            string? deviceId = InputReportBuilder.GetDeviceIdForItf(uart, itf);
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return Results.NotFound(new { ok = false, error = "deviceId not available" });
            }
            MouseMappingRecord? record = store.Get(deviceId);
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
                buttonsCount = record.ButtonsCount,
                mapping = record.Mapping,
                updatedAt = record.UpdatedAt
            });
        });

        group.MapPost("/leftClick", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("left", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MouseClickAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/rightClick", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("right", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MouseClickAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/middleClick", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("middle", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MouseClickAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/leftPress", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("left", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MousePressAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/rightPress", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("right", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MousePressAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/middlePress", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("middle", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MousePressAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/leftRelease", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("left", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MouseReleaseAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/rightRelease", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("right", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MouseReleaseAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/middleRelease", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("middle", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MouseReleaseAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/back", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("back", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MouseClickAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/forward", async (Options opt, MouseState state, HidUartClient uart, MouseMappingStore mappingStore, CancellationToken ct) =>
        {
            byte itfSel = await InterfaceSelector.ResolveItfSelAsync(null, opt.MouseTypeName, opt.MouseItfSel, uart, ct);
            if (itfSel == 0xFF) return Results.BadRequest(new { ok = false, error = "mouse_itf_unresolved" });
            byte mask = InputReportBuilder.ResolveMouseButtonMask("forward", opt, uart, mappingStore, itfSel);
            await InputReportBuilder.MouseClickAsync(state, uart, opt, itfSel, mask, ct);
            return Results.Ok(new { ok = true });
        });
    }
}
