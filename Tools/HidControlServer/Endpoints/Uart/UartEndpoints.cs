using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using HidControlServer;
using HidControlServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Encoding = global::System.Text.Encoding;

namespace HidControlServer.Endpoints.Uart;

/// <summary>
/// Registers Uart endpoints.
/// </summary>
public static class UartEndpoints
{
    /// <summary>
    /// Maps uart endpoints.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapUartEndpoints(this WebApplication app)
    {
        app.Map("/control", async (HttpContext ctx, Options opt, HidUartClient uart, MouseState mouseState, KeyboardState keyboardState, MouseMappingStore mouseMapping, KeyboardMappingStore keyboardMapping) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();
            string sessionId = Guid.NewGuid().ToString("N");
            const int maxRateHz = 250;
            const int maxBatch = 64;
            long minTicks = TimeSpan.TicksPerSecond / maxRateHz;
            long lastInputTicks = 0;
            var buffer = new byte[16 * 1024];
            CancellationToken ct = ctx.RequestAborted;

            async Task SendJsonAsync(object payload)
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload);
                await ws.SendAsync(data, WebSocketMessageType.Text, true, ct);
            }

            async Task SendAckAsync(int seq)
            {
                await SendJsonAsync(new { t = "ack", seq });
            }

            async Task SendNackAsync(int seq, string error)
            {
                await SendJsonAsync(new { t = "nack", seq, error });
            }

            async Task<string?> ReceiveTextAsync()
            {
                using var ms = new MemoryStream();
                while (true)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }
                    if (result.Count > 0)
                    {
                        ms.Write(buffer, 0, result.Count);
                    }
                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }

            async Task<bool> HandleMouseAsync(JsonElement payload, int seq)
            {
                int dx = payload.TryGetProperty("dx", out var dxProp) && dxProp.TryGetInt32(out int dxVal) ? dxVal : 0;
                int dy = payload.TryGetProperty("dy", out var dyProp) && dyProp.TryGetInt32(out int dyVal) ? dyVal : 0;
                int wheel = payload.TryGetProperty("wheel", out var whProp) && whProp.TryGetInt32(out int whVal) ? whVal : 0;
                int buttons = payload.TryGetProperty("buttons", out var btnProp) && btnProp.TryGetInt32(out int btnVal) ? btnVal : -1;
                int itf = payload.TryGetProperty("itfSel", out var itfProp) && itfProp.TryGetInt32(out int itfVal) ? itfVal : -1;

                if (!opt.MouseMoveAllowZero && dx == 0 && dy == 0 && wheel == 0 && buttons < 0)
                {
                    await SendAckAsync(seq);
                    return true;
                }

                if (buttons >= 0)
                {
                    mouseState.SetButtonsMask((byte)buttons);
                }

                byte itfSel = InterfaceSelector.ResolveItfSel(itf >= 0 ? (byte?)itf : null, opt.MouseTypeName, opt.MouseItfSel, uart);
                byte currentButtons = mouseState.GetButtons();
                byte[] report = InputReportBuilder.TryBuildMouseReport(uart, itfSel, currentButtons, dx, dy, wheel, opt.MouseReportLen);

                if (dx != 0 || dy != 0)
                {
                    await uart.SendInjectReportAsync(itfSel, report, opt.MouseMoveTimeoutMs, 0, opt.MouseMoveDropIfBusy, ct);
                }
                else if (wheel != 0)
                {
                    if (!opt.MouseWheelAllowZero && wheel == 0)
                    {
                        await SendAckAsync(seq);
                        return true;
                    }
                    await uart.SendInjectReportAsync(itfSel, report, opt.MouseWheelTimeoutMs, 0, opt.MouseWheelDropIfBusy, ct);
                }
                else
                {
                    await uart.SendInjectReportAsync(itfSel, report, opt.InjectTimeoutMs, opt.InjectRetries, false, ct);
                }

                await SendAckAsync(seq);
                return true;
            }

            async Task<bool> HandleKeyboardAsync(JsonElement payload, int seq)
            {
                string action = payload.TryGetProperty("type", out var typeProp) ? (typeProp.GetString() ?? "report") : "report";
                int itf = payload.TryGetProperty("itfSel", out var itfProp) && itfProp.TryGetInt32(out int itfVal) ? itfVal : -1;
                byte itfSel = InterfaceSelector.ResolveItfSel(itf >= 0 ? (byte?)itf : null, opt.KeyboardTypeName, opt.KeyboardItfSel, uart);
                await ReportLayoutService.EnsureReportLayoutAsync(uart, itfSel, ct);

                if (action.Equals("type", StringComparison.OrdinalIgnoreCase))
                {
                    string text = payload.TryGetProperty("text", out var textProp) ? (textProp.GetString() ?? string.Empty) : string.Empty;
                    foreach (char ch in text)
                    {
                        if (!HidReports.TryMapAsciiToHidKey(ch, out byte modifiers, out byte usage))
                        {
                            await SendNackAsync(seq, "bad_payload");
                            return false;
                        }
                        byte mappedUsage = InputReportBuilder.ResolveKeyboardUsage(uart, keyboardMapping, itfSel, usage);
                        if (InputReportBuilder.IsModifierUsage(mappedUsage))
                        {
                            byte bit = InputReportBuilder.ModifierBit(mappedUsage);
                            byte[] downReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers | bit), Array.Empty<byte>());
                            byte[] upReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers & ~bit), Array.Empty<byte>());
                            await uart.SendInjectReportAsync(itfSel, downReportMod, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                            await uart.SendInjectReportAsync(itfSel, upReportMod, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                        }
                        else
                        {
                            byte[] downReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, modifiers, new[] { mappedUsage });
                            byte[] upReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, 0, Array.Empty<byte>());
                            await uart.SendInjectReportAsync(itfSel, downReport, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                            await uart.SendInjectReportAsync(itfSel, upReport, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                        }
                    }

                    await SendAckAsync(seq);
                    return true;
                }

                int modifiersInt = payload.TryGetProperty("modifiers", out var modProp) && modProp.TryGetInt32(out int modVal) ? modVal : 0;
                int usageInt = payload.TryGetProperty("usage", out var usageProp) && usageProp.TryGetInt32(out int usageVal) ? usageVal : -1;
                bool applyMapping = payload.TryGetProperty("applyMapping", out var mapProp) ? mapProp.GetBoolean() : true;

                if (action.Equals("press", StringComparison.OrdinalIgnoreCase))
                {
                    if (usageInt < 0)
                    {
                        await SendNackAsync(seq, "bad_payload");
                        return false;
                    }
                    byte usage = InputReportBuilder.ResolveKeyboardUsage(uart, keyboardMapping, itfSel, (byte)usageInt);
                    byte modifiers = (byte)modifiersInt;
                    if (InputReportBuilder.IsModifierUsage(usage))
                    {
                        byte bit = InputReportBuilder.ModifierBit(usage);
                        byte[] downReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers | bit), Array.Empty<byte>());
                        byte[] upReportMod = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, (byte)(modifiers & ~bit), Array.Empty<byte>());
                        await uart.SendInjectReportAsync(itfSel, downReportMod, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                        await uart.SendInjectReportAsync(itfSel, upReportMod, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                    }
                    else
                    {
                        byte[] downReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, modifiers, new[] { usage });
                        byte[] upReport = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, 0, Array.Empty<byte>());
                        await uart.SendInjectReportAsync(itfSel, downReport, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                        await uart.SendInjectReportAsync(itfSel, upReport, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                    }

                    await SendAckAsync(seq);
                    return true;
                }

                if (action.Equals("down", StringComparison.OrdinalIgnoreCase) || action.Equals("up", StringComparison.OrdinalIgnoreCase))
                {
                    if (usageInt < 0)
                    {
                        await SendNackAsync(seq, "bad_payload");
                        return false;
                    }
                    byte usage = InputReportBuilder.ResolveKeyboardUsage(uart, keyboardMapping, itfSel, (byte)usageInt);
                    byte modifiers = (byte)modifiersInt;
                    if (InputReportBuilder.IsModifierUsage(usage))
                    {
                        byte bit = InputReportBuilder.ModifierBit(usage);
                        if (action.Equals("down", StringComparison.OrdinalIgnoreCase))
                        {
                            keyboardState.ModifierDown(bit, modifiers);
                        }
                        else
                        {
                            keyboardState.ModifierUp(bit, modifiers);
                        }
                    }
                    else
                    {
                        if (action.Equals("down", StringComparison.OrdinalIgnoreCase))
                        {
                            keyboardState.KeyDown(usage, modifiers);
                        }
                        else
                        {
                            keyboardState.KeyUp(usage, modifiers);
                        }
                    }

                    KeyboardSnapshot snap = keyboardState.Snapshot();
                    byte[] report = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, snap.Modifiers, snap.Keys);
                    await uart.SendInjectReportAsync(itfSel, report, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                    await SendAckAsync(seq);
                    return true;
                }

                if (action.Equals("report", StringComparison.OrdinalIgnoreCase))
                {
                    int[] keys = Array.Empty<int>();
                    if (payload.TryGetProperty("keys", out var keysProp) && keysProp.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<int>();
                        foreach (JsonElement key in keysProp.EnumerateArray())
                        {
                            if (key.TryGetInt32(out int k))
                            {
                                list.Add(k);
                            }
                        }
                        keys = list.ToArray();
                    }

                    if (keys.Length > 6)
                    {
                        await SendNackAsync(seq, "bad_payload");
                        return false;
                    }

                    byte modifiers = (byte)modifiersInt;
                    var finalKeys = new List<byte>(keys.Length);
                    if (applyMapping)
                    {
                        modifiers = InputReportBuilder.ApplyModifierMapping(uart, keyboardMapping, itfSel, modifiers, finalKeys);
                        foreach (int key in keys)
                        {
                            if (key < 0 || key > 255) continue;
                            byte mapped = InputReportBuilder.ResolveKeyboardUsage(uart, keyboardMapping, itfSel, (byte)key);
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
                        foreach (int key in keys)
                        {
                            if (key < 0 || key > 255) continue;
                            byte mapped = (byte)key;
                            if (finalKeys.Contains(mapped)) continue;
                            if (finalKeys.Count >= 6) break;
                            finalKeys.Add(mapped);
                        }
                    }

                    byte[] report = InputReportBuilder.TryBuildKeyboardReport(uart, itfSel, modifiers, finalKeys);
                    await uart.SendInjectReportAsync(itfSel, report, opt.KeyboardInjectTimeoutMs, opt.KeyboardInjectRetries, false, ct);
                    await SendAckAsync(seq);
                    return true;
                }

                await SendNackAsync(seq, "bad_payload");
                return false;
            }

            await SendJsonAsync(new { t = "welcome", v = 1, sessionId, maxRateHz });

            async Task<bool> HandleInputAsync(JsonElement root, bool enforceRateLimit)
            {
                if (!root.TryGetProperty("seq", out var seqProp) || !seqProp.TryGetInt32(out int seq))
                {
                    await SendJsonAsync(new { t = "nack", error = "bad_payload" });
                    return false;
                }

                if (enforceRateLimit)
                {
                    long nowTicks = DateTime.UtcNow.Ticks;
                    if (nowTicks - lastInputTicks < minTicks)
                    {
                        await SendNackAsync(seq, "rate_limit");
                        return false;
                    }
                    lastInputTicks = nowTicks;
                }

                if (!root.TryGetProperty("device", out var devProp))
                {
                    await SendNackAsync(seq, "bad_payload");
                    return false;
                }

                await DeviceKeyService.EnsureDeviceKeyAsync(opt, uart, ct);
                string device = devProp.GetString() ?? string.Empty;
                if (!root.TryGetProperty("payload", out var payload))
                {
                    await SendNackAsync(seq, "bad_payload");
                    return false;
                }

                if (device.Equals("mouse", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleMouseAsync(payload, seq);
                    return true;
                }
                if (device.Equals("keyboard", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleKeyboardAsync(payload, seq);
                    return true;
                }

                await SendNackAsync(seq, "bad_payload");
                return false;
            }

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                string? text = await ReceiveTextAsync();
                if (text is null) break;

                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(text);
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("t", out var tProp))
                    {
                        await SendJsonAsync(new { t = "nack", error = "bad_payload" });
                        continue;
                    }
                    string type = tProp.GetString() ?? string.Empty;

                    if (type.Equals("hello", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendJsonAsync(new { t = "welcome", v = 1, sessionId, maxRateHz });
                        continue;
                    }
                    if (type.Equals("ping", StringComparison.OrdinalIgnoreCase))
                    {
                        long ts = root.TryGetProperty("ts", out var tsProp) && tsProp.TryGetInt64(out long tsVal) ? tsVal : 0;
                        await SendJsonAsync(new { t = "pong", ts });
                        continue;
                    }
                    if (type.Equals("input", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleInputAsync(root, true);
                        continue;
                    }
                    if (type.Equals("batch", StringComparison.OrdinalIgnoreCase) || type.Equals("input_batch", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!root.TryGetProperty("items", out var itemsProp) || itemsProp.ValueKind != JsonValueKind.Array)
                        {
                            await SendJsonAsync(new { t = "nack", error = "bad_payload" });
                            continue;
                        }
                        int count = itemsProp.GetArrayLength();
                        if (count == 0)
                        {
                            await SendJsonAsync(new { t = "ack", count = 0 });
                            continue;
                        }
                        if (count > maxBatch)
                        {
                            await SendJsonAsync(new { t = "nack", error = "batch_too_large" });
                            continue;
                        }
                        foreach (JsonElement item in itemsProp.EnumerateArray())
                        {
                            await HandleInputAsync(item, false);
                        }
                        lastInputTicks = DateTime.UtcNow.Ticks;
                        continue;
                    }

                    await SendJsonAsync(new { t = "nack", error = "bad_payload" });
                }
                catch
                {
                    await SendJsonAsync(new { t = "nack", error = "bad_payload" });
                }
                finally
                {
                    doc?.Dispose();
                }
            }
        });

        app.Map("/status", async (HttpContext ctx, Options opt, HidUartClient uart, KeyboardState keyboard, MouseState mouse) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();
            CancellationToken ct = ctx.RequestAborted;
            var buffer = new byte[4096];
            const int intervalMs = 1000;

            async Task SendJsonAsync(object payload)
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload);
                await ws.SendAsync(data, WebSocketMessageType.Text, true, ct);
            }

            object BuildStatus()
            {
                HidUartClientStats uartStats = uart.GetStats();
                KeyboardSnapshot keyboardSnapshot = keyboard.Snapshot();
                byte buttons = mouse.GetButtons();
                long uptimeMs = (long)(DateTimeOffset.UtcNow - uartStats.StartedAt).TotalMilliseconds;
                string hmacMode = uart.IsBootstrapForced() ? "bootstrap" : (uart.HasDerivedKey() ? "derived" : "bootstrap");
                return new
                {
                    t = "status",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    uptimeMs,
                    hmacMode,
                    uart = uartStats,
                    deviceId = uart.GetDeviceIdHex(),
                    mouse = new { buttons },
                    keyboard = keyboardSnapshot
                };
            }

            await SendJsonAsync(BuildStatus());

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                Task<WebSocketReceiveResult> recvTask = ws.ReceiveAsync(buffer, ct);
                Task delayTask = Task.Delay(intervalMs, ct);
                Task done = await Task.WhenAny(recvTask, delayTask);
                if (done == delayTask)
                {
                    await SendJsonAsync(BuildStatus());
                    continue;
                }

                WebSocketReceiveResult result = await recvTask;
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("t", out var tProp) && tProp.GetString() == "ping")
                    {
                        long ts = root.TryGetProperty("ts", out var tsProp) && tsProp.TryGetInt64(out long tsVal) ? tsVal : 0;
                        await SendJsonAsync(new { t = "pong", ts });
                    }
                }
                catch
                {
                    // ignore malformed status requests
                }
            }
        });


        app.MapGet("/device-id", async (Options opt, HidUartClient uart, CancellationToken ct) =>
        {
            await DeviceKeyService.EnsureDeviceKeyAsync(opt, uart, ct);
            string? id = uart.GetDeviceIdHex();
            string hmacMode = uart.IsBootstrapForced() ? "bootstrap" : (uart.HasDerivedKey() ? "derived" : "bootstrap");
            return Results.Ok(new
            {
                ok = true,
                deviceId = id,
                derivedKeyActive = uart.HasDerivedKey(),
                hmacMode
            });
        });

        app.MapGet("/uart/ping", async (int? timeoutMs, HidUartClient uart, CancellationToken ct) =>
        {
            int timeout = Math.Clamp(timeoutMs ?? 500, 100, 3000);
            var sw = Stopwatch.StartNew();
            byte[]? id = await uart.RequestDeviceIdAsync(timeout, ct);
            sw.Stop();
            string hmacMode = uart.IsBootstrapForced() ? "bootstrap" : (uart.HasDerivedKey() ? "derived" : "bootstrap");
            if (id is null)
            {
                return Results.Ok(new { ok = false, error = "timeout", elapsedMs = sw.ElapsedMilliseconds, hmacMode });
            }
            return Results.Ok(new
            {
                ok = true,
                elapsedMs = sw.ElapsedMilliseconds,
                deviceId = Convert.ToHexString(id),
                hmacMode
            });
        });

        app.MapGet("/uart/trace", (int? limit, HidUartClient uart) =>
        {
            int take = Math.Clamp(limit ?? 20, 1, 80);
            var trace = uart.GetTraceSnapshot(take).Select(t => new
            {
                ts = t.Ts,
                dir = t.Dir,
                info = t.Info,
                frameHex = t.FrameHex
            }).ToList();
            return Results.Ok(new { ok = true, trace });
        });

        app.MapPost("/hmac/retry", async (Options opt, HidUartClient uart, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(opt.MasterSecret))
            {
                return Results.BadRequest(new { ok = false, error = "masterSecret is not set" });
            }

            uart.ClearBootstrapForce();
            await DeviceKeyService.InitializeDeviceKeyAsync(opt, uart, ct);
            string hmacMode = uart.IsBootstrapForced() ? "bootstrap" : (uart.HasDerivedKey() ? "derived" : "bootstrap");
            return Results.Ok(new { ok = true, hmacMode });
        });

        app.MapPost("/loglevel", async (LogLevelRequest req, HidUartClient uart, CancellationToken ct) =>
        {
            if (req.Level > 4) return Results.BadRequest(new { ok = false, error = "level must be 0..4" });
            await uart.SetLogLevelAsync(req.Level, ct);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/raw/inject", async (RawInjectRequest req, HidUartClient uart, Options opt, CancellationToken ct) =>
        {
            if (req.ReportHex is null) return Results.BadRequest(new { ok = false, error = "reportHex is required" });
            byte[] report = Hex.Parse(req.ReportHex);
            await uart.SendInjectReportAsync(req.ItfSel, report, opt.InjectTimeoutMs, opt.InjectRetries, false, ct);
            return Results.Ok(new { ok = true });
        });
    }
}
