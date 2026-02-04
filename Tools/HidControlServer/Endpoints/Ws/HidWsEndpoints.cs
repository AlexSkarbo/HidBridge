using System.Net.WebSockets;
using System.Text.Json;
using HidControlServer;
using HidControlServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Encoding = global::System.Text.Encoding;

namespace HidControlServer.Endpoints.Ws;

/// <summary>
/// Registers HidWs endpoints.
/// </summary>
public static class HidWsEndpoints
{
    /// <summary>
    /// Maps hid ws endpoints.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapHidWsEndpoints(this WebApplication app)
    {
        app.Map("/ws/hid", (Func<HttpContext, Task>)(async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var opt = ctx.RequestServices.GetRequiredService<Options>();
            var uart = ctx.RequestServices.GetRequiredService<HidUartClient>();
            var appState = ctx.RequestServices.GetRequiredService<AppState>();
            var mouseState = ctx.RequestServices.GetRequiredService<MouseState>();
            var keyboardState = ctx.RequestServices.GetRequiredService<KeyboardState>();
            var mouseMapping = ctx.RequestServices.GetRequiredService<MouseMappingStore>();
            var keyboardMapping = ctx.RequestServices.GetRequiredService<KeyboardMappingStore>();
            string mouseTypeName = opt.MouseTypeName ?? "mouse";
            string keyboardTypeName = opt.KeyboardTypeName ?? "keyboard";

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[16 * 1024];
            var ms = new MemoryStream();

            Task<byte> ResolveItfSelWithCacheAsync(byte? requestItfSel, string typeName, byte fallbackItfSel, byte unresolved, CancellationToken ct)
            {
                if (requestItfSel is null && InterfaceSelector.TryResolveItfSelFromCache(appState, typeName, out byte cached))
                {
                    return Task.FromResult(cached);
                }

                byte itfSel = InterfaceSelector.ResolveItfSel(requestItfSel, typeName, fallbackItfSel, uart);
                if (itfSel != unresolved)
                {
                    return Task.FromResult(itfSel);
                }

                if (InterfaceSelector.TryResolveItfSelFromCache(appState, typeName, out cached))
                {
                    return Task.FromResult(cached);
                }

                return Task.FromResult(itfSel);
            }

            async Task SendAsync(object payload, CancellationToken ct)
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload);
                await socket.SendAsync(data, WebSocketMessageType.Text, true, ct);
            }

            while (!ctx.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult? result = null;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ctx.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", ctx.RequestAborted);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await SendAsync(new { ok = false, error = "binary_not_supported" }, ctx.RequestAborted);
                    continue;
                }

                string json = Encoding.UTF8.GetString(ms.ToArray());
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(json);
                }
                catch
                {
                    await SendAsync(new { ok = false, error = "invalid_json" }, ctx.RequestAborted);
                    continue;
                }

                using (doc)
                {
                    JsonElement root = doc.RootElement;
                    string type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                    string? msgId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    try
                    {
                        switch (type)
                        {
                            case "ping":
                                await SendAsync(new { ok = true, type = "pong", id = msgId }, ctx.RequestAborted);
                                break;
                            case "mouse.move":
                            {
                                int dx = GetInt(root, "dx");
                                int dy = GetInt(root, "dy");
                                int wheel = GetInt(root, "wheel");
                                if (!opt.MouseMoveAllowZero && dx == 0 && dy == 0 && wheel == 0)
                                {
                                    await SendAsync(new { ok = true, type, id = msgId, skipped = true }, ctx.RequestAborted);
                                    break;
                                }
                                byte itfSel = await ResolveItfSelWithCacheAsync(GetByteNullable(root, "itfSel"), mouseTypeName, opt.MouseItfSel, 0xFF, ctx.RequestAborted);
                                if (itfSel == 0xFF)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "mouse_itf_unresolved" }, ctx.RequestAborted);
                                    break;
                                }
                                byte buttons = mouseState.GetButtons();
                                await ReportLayoutService.EnsureMouseLayoutAsync(uart, itfSel, ctx.RequestAborted);
                                byte[] report = InputReportBuilder.TryBuildMouseReport(uart, itfSel, buttons, dx, dy, wheel, opt.MouseReportLen);
                                await uart.SendInjectReportAsync(itfSel, report, opt.MouseMoveTimeoutMs, 0, opt.MouseMoveDropIfBusy, ctx.RequestAborted);
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "mouse.wheel":
                            {
                                int delta = GetInt(root, "delta");
                                if (!opt.MouseWheelAllowZero && delta == 0)
                                {
                                    await SendAsync(new { ok = true, type, id = msgId, skipped = true }, ctx.RequestAborted);
                                    break;
                                }
                                byte itfSel = await ResolveItfSelWithCacheAsync(GetByteNullable(root, "itfSel"), mouseTypeName, opt.MouseItfSel, 0xFF, ctx.RequestAborted);
                                if (itfSel == 0xFF)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "mouse_itf_unresolved" }, ctx.RequestAborted);
                                    break;
                                }
                                await ReportLayoutService.EnsureMouseLayoutAsync(uart, itfSel, ctx.RequestAborted);
                                byte[] report = InputReportBuilder.TryBuildMouseReport(uart, itfSel, mouseState.GetButtons(), 0, 0, delta, opt.MouseReportLen);
                                await uart.SendInjectReportAsync(itfSel, report, opt.MouseWheelTimeoutMs, 0, opt.MouseWheelDropIfBusy, ctx.RequestAborted);
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "mouse.buttons":
                            {
                                byte mask = (byte)GetInt(root, "mask");
                                mouseState.SetButtonsMask(mask);
                                byte itfSel = await ResolveItfSelWithCacheAsync(GetByteNullable(root, "itfSel"), mouseTypeName, opt.MouseItfSel, 0xFF, ctx.RequestAborted);
                                if (itfSel == 0xFF)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "mouse_itf_unresolved" }, ctx.RequestAborted);
                                    break;
                                }
                                await ReportLayoutService.EnsureMouseLayoutAsync(uart, itfSel, ctx.RequestAborted);
                                byte[] report = InputReportBuilder.TryBuildMouseReport(uart, itfSel, mouseState.GetButtons(), 0, 0, 0, opt.MouseReportLen);
                                await uart.SendInjectReportAsync(itfSel, report, opt.InjectTimeoutMs, opt.InjectRetries, false, ctx.RequestAborted);
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "mouse.button":
                            {
                                string button = root.TryGetProperty("button", out var b) ? (b.GetString() ?? "") : "";
                                bool down = root.TryGetProperty("down", out var d) && d.ValueKind == JsonValueKind.True;
                                byte itfSel = await ResolveItfSelWithCacheAsync(GetByteNullable(root, "itfSel"), mouseTypeName, opt.MouseItfSel, 0xFF, ctx.RequestAborted);
                                if (itfSel == 0xFF)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "mouse_itf_unresolved" }, ctx.RequestAborted);
                                    break;
                                }
                                byte mask = InputReportBuilder.ResolveMouseButtonMask(button, opt, uart, mouseMapping, itfSel);
                                InputReportBuilder.ApplyMouseButtonMask(mouseState, mask, down);
                                await ReportLayoutService.EnsureMouseLayoutAsync(uart, itfSel, ctx.RequestAborted);
                                byte[] report = InputReportBuilder.TryBuildMouseReport(uart, itfSel, mouseState.GetButtons(), 0, 0, 0, opt.MouseReportLen);
                                await uart.SendInjectReportAsync(itfSel, report, opt.InjectTimeoutMs, opt.InjectRetries, false, ctx.RequestAborted);
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.down":
                            {
                                byte usage = (byte)GetInt(root, "usage");
                                byte? mods = GetByteNullable(root, "mods") ?? GetByteNullable(root, "modifiers");
                                byte itfSel = await ResolveItfSelWithCacheAsync(GetByteNullable(root, "itfSel"), keyboardTypeName, opt.KeyboardItfSel, 0xFE, ctx.RequestAborted);
                                if (itfSel == 0xFE)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "keyboard_itf_unresolved" }, ctx.RequestAborted);
                                    break;
                                }
                                byte mapped = InputReportBuilder.ResolveKeyboardUsage(uart, keyboardMapping, itfSel, usage);
                                if (InputReportBuilder.IsModifierUsage(mapped))
                                {
                                    keyboardState.ModifierDown(InputReportBuilder.ModifierBit(mapped), mods);
                                }
                                else
                                {
                                    keyboardState.KeyDown(mapped, mods);
                                }
                                KeyboardSnapshot snap = keyboardState.Snapshot();
                                await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ctx.RequestAborted);
                                byte[] report = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, snap.Modifiers, snap.Keys);
                                await uart.SendInjectReportAsync(itfSel, report, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.up":
                            {
                                byte usage = (byte)GetInt(root, "usage");
                                byte? mods = GetByteNullable(root, "mods") ?? GetByteNullable(root, "modifiers");
                                byte itfSel = await ResolveItfSelWithCacheAsync(GetByteNullable(root, "itfSel"), keyboardTypeName, opt.KeyboardItfSel, 0xFE, ctx.RequestAborted);
                                if (itfSel == 0xFE)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "keyboard_itf_unresolved" }, ctx.RequestAborted);
                                    break;
                                }
                                byte mapped = InputReportBuilder.ResolveKeyboardUsage(uart, keyboardMapping, itfSel, usage);
                                if (InputReportBuilder.IsModifierUsage(mapped))
                                {
                                    keyboardState.ModifierUp(InputReportBuilder.ModifierBit(mapped), mods);
                                }
                                else
                                {
                                    keyboardState.KeyUp(mapped, mods);
                                }
                                KeyboardSnapshot snap = keyboardState.Snapshot();
                                await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ctx.RequestAborted);
                                byte[] report = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, snap.Modifiers, snap.Keys);
                                await uart.SendInjectReportAsync(itfSel, report, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.press":
                            {
                                byte usage = (byte)GetInt(root, "usage");
                                byte modifiers = (byte)GetInt(root, "mods", "modifiers");
                                byte itfSel = await ResolveItfSelWithCacheAsync(GetByteNullable(root, "itfSel"), keyboardTypeName, opt.KeyboardItfSel, 0xFE, ctx.RequestAborted);
                                if (itfSel == 0xFE)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "keyboard_itf_unresolved" }, ctx.RequestAborted);
                                    break;
                                }
                                await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ctx.RequestAborted);
                                byte mapped = InputReportBuilder.ResolveKeyboardUsage(uart, keyboardMapping, itfSel, usage);
                                if (InputReportBuilder.IsModifierUsage(mapped))
                                {
                                    byte bit = InputReportBuilder.ModifierBit(mapped);
                                    byte[] downReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers | bit), Array.Empty<byte>());
                                    byte[] upReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers & ~bit), Array.Empty<byte>());
                                    await uart.SendInjectReportAsync(itfSel, downReportMod, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                    await uart.SendInjectReportAsync(itfSel, upReportMod, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                }
                                else
                                {
                                    byte[] downReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, modifiers, new[] { mapped });
                                    byte[] upReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, 0, Array.Empty<byte>());
                                    await uart.SendInjectReportAsync(itfSel, downReport, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                    await uart.SendInjectReportAsync(itfSel, upReport, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.text":
                            {
                                string text = root.TryGetProperty("text", out var te) ? (te.GetString() ?? "") : "";
                                if (string.IsNullOrEmpty(text))
                                {
                                    await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                    break;
                                }
                                byte itfSel = await ResolveItfSelWithCacheAsync(GetByteNullable(root, "itfSel"), keyboardTypeName, opt.KeyboardItfSel, 0xFE, ctx.RequestAborted);
                                if (itfSel == 0xFE)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "keyboard_itf_unresolved" }, ctx.RequestAborted);
                                    break;
                                }
                                await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ctx.RequestAborted);
                                foreach (char ch in text)
                                {
                                    if (!HidReports.TryMapAsciiToHidKey(ch, out byte modifiers, out byte usage))
                                    {
                                        await SendAsync(new { ok = false, type, id = msgId, error = $"unsupported_char_{(int)ch:X4}" }, ctx.RequestAborted);
                                        return;
                                    }
                                    byte mapped = InputReportBuilder.ResolveKeyboardUsage(uart, keyboardMapping, itfSel, usage);
                                    if (InputReportBuilder.IsModifierUsage(mapped))
                                    {
                                        byte bit = InputReportBuilder.ModifierBit(mapped);
                                        byte[] downReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers | bit), Array.Empty<byte>());
                                        byte[] upReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers & ~bit), Array.Empty<byte>());
                                        await uart.SendInjectReportAsync(itfSel, downReportMod, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                        await uart.SendInjectReportAsync(itfSel, upReportMod, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                        continue;
                                    }
                                    byte[] downReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, modifiers, new[] { mapped });
                                    byte[] upReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, 0, Array.Empty<byte>());
                                    await uart.SendInjectReportAsync(itfSel, downReport, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                    await uart.SendInjectReportAsync(itfSel, upReport, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.report":
                            {
                                byte itfSel = await ResolveItfSelWithCacheAsync(GetByteNullable(root, "itfSel"), keyboardTypeName, opt.KeyboardItfSel, 0xFE, ctx.RequestAborted);
                                if (itfSel == 0xFE)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "keyboard_itf_unresolved" }, ctx.RequestAborted);
                                    break;
                                }
                                await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ctx.RequestAborted);
                                byte modifiers = (byte)GetInt(root, "mods", "modifiers");
                                bool applyMapping = GetBool(root, "applyMapping", true);
                                var keys = GetIntArray(root, "keys");
                                if (keys.Count > 6)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "keys_max_6" }, ctx.RequestAborted);
                                    break;
                                }
                                var keyBytes = keys.Select(k => (byte)Math.Clamp(k, 0, 255)).ToList();
                                var finalKeys = new List<byte>(keyBytes.Count);
                                if (applyMapping)
                                {
                                    modifiers = InputReportBuilder.ApplyModifierMapping(uart, keyboardMapping, itfSel, modifiers, finalKeys);
                                    foreach (byte key in keyBytes)
                                    {
                                        byte mapped = InputReportBuilder.ResolveKeyboardUsage(uart, keyboardMapping, itfSel, key);
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
                                await uart.SendInjectReportAsync(itfSel, report, opt.KeyboardInjectTimeoutMs, 0, false, ctx.RequestAborted);
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            default:
                                await SendAsync(new { ok = false, error = "unknown_type", type, id = msgId }, ctx.RequestAborted);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        await SendAsync(new { ok = false, error = ex.Message, type, id = msgId }, ctx.RequestAborted);
                    }
                }
            }
        }));
    }

    /// <summary>
    /// Gets int.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="names">The names.</param>
    /// <returns>Result.</returns>
    private static int GetInt(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (root.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int num)) return num;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int strNum)) return strNum;
            }
        }
        return 0;
    }

    /// <summary>
    /// Gets byte nullable.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="name">The name.</param>
    /// <returns>Result.</returns>
    private static byte? GetByteNullable(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int num)) return (byte)num;
        if (el.ValueKind == JsonValueKind.String && byte.TryParse(el.GetString(), out byte strNum)) return strNum;
        return null;
    }

    /// <summary>
    /// Gets bool.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="name">The name.</param>
    /// <param name="fallback">The fallback.</param>
    /// <returns>Result.</returns>
    private static bool GetBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        return fallback;
    }

    /// <summary>
    /// Gets int array.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="name">The name.</param>
    /// <returns>Result.</returns>
    private static List<int> GetIntArray(JsonElement root, string name)
    {
        var list = new List<int>();
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in el.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int num))
            {
                list.Add(num);
                continue;
            }
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int strNum))
            {
                list.Add(strNum);
            }
        }
        return list;
    }
}
