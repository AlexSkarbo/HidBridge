using System.Net.WebSockets;
using System.Text.Json;
using HidControl.Application.UseCases;
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

            var keyboardPressUseCase = ctx.RequestServices.GetRequiredService<KeyboardPressUseCase>();
            var keyboardDownUseCase = ctx.RequestServices.GetRequiredService<KeyboardDownUseCase>();
            var keyboardUpUseCase = ctx.RequestServices.GetRequiredService<KeyboardUpUseCase>();
            var keyboardTextUseCase = ctx.RequestServices.GetRequiredService<KeyboardTextUseCase>();
            var keyboardShortcutUseCase = ctx.RequestServices.GetRequiredService<KeyboardShortcutUseCase>();
            var keyboardReportUseCase = ctx.RequestServices.GetRequiredService<KeyboardReportUseCase>();
            var keyboardResetUseCase = ctx.RequestServices.GetRequiredService<KeyboardResetUseCase>();
            var mouseMoveUseCase = ctx.RequestServices.GetRequiredService<MouseMoveUseCase>();
            var mouseWheelUseCase = ctx.RequestServices.GetRequiredService<MouseWheelUseCase>();
            var mouseButtonsMaskUseCase = ctx.RequestServices.GetRequiredService<MouseButtonsMaskUseCase>();
            var mouseButtonUseCase = ctx.RequestServices.GetRequiredService<MouseButtonUseCase>();

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[16 * 1024];
            var ms = new MemoryStream();

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
                                int dx = HidWsJson.GetInt(root, "dx");
                                int dy = HidWsJson.GetInt(root, "dy");
                                int wheel = HidWsJson.GetInt(root, "wheel");
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                var moveRes = await mouseMoveUseCase.ExecuteAsync(new HidControl.Contracts.MouseMoveRequest(dx, dy, wheel, itfSelReq), ctx.RequestAborted);
                                if (!moveRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = moveRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }
                                if (moveRes.Skipped)
                                {
                                    await SendAsync(new { ok = true, type, id = msgId, skipped = true }, ctx.RequestAborted);
                                    break;
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "mouse.wheel":
                            {
                                int delta = HidWsJson.GetInt(root, "delta");
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                var wheelRes = await mouseWheelUseCase.ExecuteAsync(new HidControl.Contracts.MouseWheelRequest(delta, itfSelReq), ctx.RequestAborted);
                                if (!wheelRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = wheelRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }
                                if (wheelRes.Skipped)
                                {
                                    await SendAsync(new { ok = true, type, id = msgId, skipped = true }, ctx.RequestAborted);
                                    break;
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "mouse.buttons":
                            {
                                byte mask = (byte)HidWsJson.GetInt(root, "mask", "buttonsMask");
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                var buttonsRes = await mouseButtonsMaskUseCase.ExecuteAsync(new HidControl.Contracts.MouseButtonsMaskRequest(mask, itfSelReq), ctx.RequestAborted);
                                if (!buttonsRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = buttonsRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "mouse.button":
                            {
                                string button = root.TryGetProperty("button", out var b) ? (b.GetString() ?? "") : "";
                                bool down = root.TryGetProperty("down", out var d) && d.ValueKind == JsonValueKind.True;
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                var btnRes = await mouseButtonUseCase.ExecuteAsync(new HidControl.Contracts.MouseButtonRequest(button, down, itfSelReq), ctx.RequestAborted);
                                if (!btnRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = btnRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.down":
                            {
                                byte usage = (byte)HidWsJson.GetInt(root, "usage");
                                byte? mods = HidWsJson.GetByteNullable(root, "mods") ?? HidWsJson.GetByteNullable(root, "modifiers");
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                var downRes = await keyboardDownUseCase.ExecuteAsync(new HidControl.Contracts.KeyboardPressRequest(usage, mods, itfSelReq), ctx.RequestAborted);
                                if (!downRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = downRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.up":
                            {
                                byte usage = (byte)HidWsJson.GetInt(root, "usage");
                                byte? mods = HidWsJson.GetByteNullable(root, "mods") ?? HidWsJson.GetByteNullable(root, "modifiers");
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                var upRes = await keyboardUpUseCase.ExecuteAsync(new HidControl.Contracts.KeyboardPressRequest(usage, mods, itfSelReq), ctx.RequestAborted);
                                if (!upRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = upRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.press":
                            {
                                byte usage = (byte)HidWsJson.GetInt(root, "usage");
                                byte modifiers = (byte)HidWsJson.GetInt(root, "mods", "modifiers");
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");

                                var pressRes = await keyboardPressUseCase.ExecuteAsync(new HidControl.Contracts.KeyboardPressRequest(usage, modifiers, itfSelReq), ctx.RequestAborted);
                                if (!pressRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = pressRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }

                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.shortcut":
                            {
                                string shortcut = root.TryGetProperty("shortcut", out var se) ? (se.GetString() ?? "") : "";
                                int holdMs = HidWsJson.GetInt(root, "holdMs");
                                bool applyMapping = HidWsJson.GetBool(root, "applyMapping", true);

                                if (holdMs < 0) holdMs = 0;
                                if (holdMs > 5000) holdMs = 5000;

                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                var shortcutRes = await keyboardShortcutUseCase.ExecuteAsync(shortcut, holdMs, applyMapping, itfSelReq, ctx.RequestAborted);
                                if (!shortcutRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = shortcutRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }

                                await SendAsync(new { ok = true, type, id = msgId, itfSel = shortcutRes.ItfSel, shortcut, modifiers = shortcutRes.Modifiers, keys = shortcutRes.Keys }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.text":
                            {
                                string text = root.TryGetProperty("text", out var te) ? (te.GetString() ?? "") : "";
                                string? layout = root.TryGetProperty("layout", out var lo) ? lo.GetString() : null;
                                if (string.IsNullOrEmpty(text))
                                {
                                    await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                    break;
                                }
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                var textRes = await keyboardTextUseCase.ExecuteAsync(text, layout, itfSelReq, ctx.RequestAborted);
                                if (!textRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = textRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                                break;
                            }
                            case "keyboard.report":
                            {
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                int modifiersInt = HidWsJson.GetInt(root, "mods", "modifiers");
                                if (modifiersInt < 0 || modifiersInt > 255)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "modifiers_out_of_range" }, ctx.RequestAborted);
                                    break;
                                }
                                byte modifiers = (byte)modifiersInt;
                                bool applyMapping = HidWsJson.GetBool(root, "applyMapping", true);
                                var keysList = HidWsJson.GetIntArray(root, "keys");
                                if (keysList.Count > 6)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = "keys_max_6" }, ctx.RequestAborted);
                                    break;
                                }
                                var keys = new int[keysList.Count];
                                for (int i = 0; i < keysList.Count; i++)
                                {
                                    int key = keysList[i];
                                    if (key < 0 || key > 255)
                                    {
                                        await SendAsync(new { ok = false, type, id = msgId, error = "keys_out_of_range" }, ctx.RequestAborted);
                                        goto KeyboardReportDone;
                                    }
                                    keys[i] = key;
                                }

                                var reportRes = await keyboardReportUseCase.ExecuteAsync(new HidControl.Contracts.KeyboardReportRequest(modifiers, keys, itfSelReq, applyMapping), ctx.RequestAborted);
                                if (!reportRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = reportRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }
                                await SendAsync(new { ok = true, type, id = msgId }, ctx.RequestAborted);
                            KeyboardReportDone:
                                break;
                            }
                            case "keyboard.reset":
                            {
                                byte? itfSelReq = HidWsJson.GetByteNullable(root, "itfSel");
                                var resetRes = await keyboardResetUseCase.ExecuteAsync(new HidControl.Contracts.KeyboardResetRequest(itfSelReq), ctx.RequestAborted);
                                if (!resetRes.Ok)
                                {
                                    await SendAsync(new { ok = false, type, id = msgId, error = resetRes.Error ?? "failed" }, ctx.RequestAborted);
                                    break;
                                }
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
}
