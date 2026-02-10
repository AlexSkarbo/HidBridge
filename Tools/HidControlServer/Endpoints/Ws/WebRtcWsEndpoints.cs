using global::System.Collections.Concurrent;
using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using HidControl.Application.Abstractions;
using HidControl.Application.UseCases.WebRtc;
using Microsoft.Extensions.DependencyInjection;

namespace HidControlServer.Endpoints.Ws;

/// <summary>
/// WebRTC signaling WebSocket endpoint.
/// 
/// This is a minimal relay for SDP/ICE messages between peers.
/// It intentionally stores state in-memory (no persistence) and is meant as a skeleton.
/// </summary>
public static class WebRtcWsEndpoints
{
    private static readonly ConcurrentDictionary<string, ClientState> Clients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps the WebRTC signaling endpoint at <c>/ws/webrtc</c>.
    /// </summary>
    /// <param name="app">Web application.</param>
    public static void MapWebRtcWsEndpoints(this WebApplication app)
    {
        app.Map("/ws/webrtc", async ctx =>
        {
            var signaling = ctx.RequestServices.GetRequiredService<IWebRtcSignalingService>();
            var handleMessageUseCase = ctx.RequestServices.GetRequiredService<HandleWebRtcSignalingMessageUseCase>();
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("expected websocket request", ctx.RequestAborted);
                return;
            }

            using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();

            string clientId = Guid.NewGuid().ToString("N");
            string? roomId = null;
            var client = new ClientState(clientId, ws);
            Clients[clientId] = client;

            try
            {
                // Initial hello.
                await client.SendJsonAsync(new
                {
                    ok = true,
                    type = "webrtc.hello",
                    clientId
                }, ctx.RequestAborted);

                var buffer = new byte[64 * 1024];
                while (!ctx.RequestAborted.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    string? msg = await ReceiveTextAsync(ws, buffer, ctx.RequestAborted);
                    if (msg is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(msg))
                    {
                        continue;
                    }

                    if (!TryParseMessage(msg, out var mType, out var mRoom, out JsonElement mData, out string? parseError))
                    {
                        await client.SendJsonAsync(new { ok = false, type = "webrtc.error", error = parseError ?? "bad_request" }, ctx.RequestAborted);
                        continue;
                    }

                    HandleWebRtcSignalingResult handled = handleMessageUseCase.Execute(roomId, mRoom, clientId, mType);

                    if (handled.Action == WebRtcSignalingAction.Join)
                    {
                        if (!handled.Ok || string.IsNullOrWhiteSpace(handled.Room) || handled.Kind is null)
                        {
                            await client.SendJsonAsync(new
                            {
                                ok = false,
                                type = "webrtc.error",
                                error = handled.Error ?? "join_failed",
                                room = handled.Room,
                                peers = handled.Peers
                            }, ctx.RequestAborted);
                            roomId = handled.NextRoom;
                            continue;
                        }

                        string joinedRoom = handled.Room;
                        roomId = handled.NextRoom;
                        await client.SendJsonAsync(new
                        {
                            ok = true,
                            type = "webrtc.joined",
                            room = joinedRoom,
                            kind = handled.Kind.Value.ToString().ToLowerInvariant(),
                            clientId,
                            peers = handled.Peers
                        }, ctx.RequestAborted);

                        await BroadcastAsync(signaling, joinedRoom, clientId, new
                        {
                            ok = true,
                            type = "webrtc.peer_joined",
                            room = joinedRoom,
                            kind = handled.Kind.Value.ToString().ToLowerInvariant(),
                            peerId = clientId,
                            peers = handled.Peers
                        }, ctx.RequestAborted);

                        continue;
                    }

                    if (handled.Action == WebRtcSignalingAction.Signal)
                    {
                        if (!handled.Ok || handled.Room is not string signalRoom || string.IsNullOrWhiteSpace(signalRoom))
                        {
                            await client.SendJsonAsync(new { ok = false, type = "webrtc.error", error = handled.Error ?? "not_joined" }, ctx.RequestAborted);
                            continue;
                        }
                        roomId = handled.NextRoom;
                        await BroadcastAsync(signaling, signalRoom, clientId, new
                        {
                            ok = true,
                            type = "webrtc.signal",
                            room = signalRoom,
                            from = clientId,
                            data = mData
                        }, ctx.RequestAborted);

                        continue;
                    }

                    if (handled.Action == WebRtcSignalingAction.Leave)
                    {
                        if (handled.HasPeerLeftBroadcast &&
                            handled.Room is string leavingRoom &&
                            !string.IsNullOrWhiteSpace(leavingRoom) &&
                            handled.Kind is not null)
                        {
                            await BroadcastAsync(signaling, leavingRoom, clientId, new
                            {
                                ok = true,
                                type = "webrtc.peer_left",
                                room = leavingRoom,
                                kind = handled.Kind.Value.ToString().ToLowerInvariant(),
                                peerId = clientId,
                                peers = handled.PeersLeft
                            }, ctx.RequestAborted);
                        }
                        roomId = handled.NextRoom;
                        await client.SendJsonAsync(new { ok = true, type = "webrtc.left", clientId }, ctx.RequestAborted);
                        continue;
                    }

                    if (handled.Action == WebRtcSignalingAction.Ping)
                    {
                        roomId = handled.NextRoom;
                        await client.SendJsonAsync(new { ok = true, type = "webrtc.pong", clientId }, ctx.RequestAborted);
                        continue;
                    }

                    roomId = handled.NextRoom;
                    await client.SendJsonAsync(new { ok = false, type = "webrtc.error", error = handled.Error ?? "unsupported_type" }, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal on request abort.
            }
            catch (WebSocketException)
            {
                // Client disconnected.
            }
            finally
            {
                if (roomId is not null)
                {
                    string finalRoom = roomId;
                    HandleWebRtcSignalingResult leaveHandled = handleMessageUseCase.Execute(finalRoom, null, clientId, "leave");
                    if (leaveHandled.HasPeerLeftBroadcast && !string.IsNullOrWhiteSpace(leaveHandled.Room) && leaveHandled.Kind is not null)
                    {
                        await BroadcastAsync(signaling, finalRoom, clientId, new
                        {
                            ok = true,
                            type = "webrtc.peer_left",
                            room = finalRoom,
                            kind = leaveHandled.Kind.Value.ToString().ToLowerInvariant(),
                            peerId = clientId,
                            peers = leaveHandled.PeersLeft
                        }, CancellationToken.None);
                    }
                }
                Clients.TryRemove(clientId, out _);
            }
        });
    }

    private static async Task BroadcastAsync(IWebRtcSignalingService signaling, string roomId, string senderId, object payload, CancellationToken ct)
    {
        foreach (string peerId in signaling.GetPeerIds(roomId, senderId))
        {
            if (Clients.TryGetValue(peerId, out ClientState? peer))
            {
                await peer.SendJsonAsync(payload, ct);
            }
        }
    }

    private static bool TryParseMessage(string json, out string type, out string? room, out JsonElement data, out string? error)
    {
        type = string.Empty;
        room = null;
        data = default;
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "root_must_be_object";
                return false;
            }

            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                error = "type_required";
                return false;
            }
            type = typeEl.GetString() ?? string.Empty;

            if (root.TryGetProperty("room", out var roomEl) && roomEl.ValueKind == JsonValueKind.String)
            {
                room = roomEl.GetString();
            }

            if (root.TryGetProperty("data", out var dataEl))
            {
                data = dataEl.Clone();
            }
            else
            {
                data = JsonDocument.Parse("{}").RootElement.Clone();
            }

            return true;
        }
        catch (JsonException)
        {
            error = "invalid_json";
            return false;
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var seg = new ArraySegment<byte>(buffer);
            WebSocketReceiveResult result = await ws.ReceiveAsync(seg, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                // Ignore binary frames.
                if (result.EndOfMessage)
                {
                    return string.Empty;
                }
                continue;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return sb.ToString();
            }
        }
    }

    private sealed class ClientState
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        /// <summary>Client identifier (server generated).</summary>
        public string Id { get; }

        /// <summary>Underlying WebSocket.</summary>
        public WebSocket Socket { get; }

        public ClientState(string id, WebSocket socket)
        {
            Id = id;
            Socket = socket;
        }

        public async Task SendJsonAsync(object payload, CancellationToken ct)
        {
            if (Socket.State != WebSocketState.Open)
            {
                return;
            }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            await _sendLock.WaitAsync(ct);
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
