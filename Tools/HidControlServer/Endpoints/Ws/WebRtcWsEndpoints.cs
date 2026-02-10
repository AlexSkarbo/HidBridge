using global::System.Collections.Concurrent;
using global::System.Net.WebSockets;
using global::System.Text;
using global::System.Text.Json;
using HidControl.Application.Abstractions;
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

                    if (string.Equals(mType, "join", StringComparison.OrdinalIgnoreCase))
                    {
                        WebRtcRoomNormalizationResult normalized = signaling.NormalizeRoom(mRoom);
                        if (!normalized.Ok || string.IsNullOrWhiteSpace(normalized.Room))
                        {
                            await client.SendJsonAsync(new { ok = false, type = "webrtc.error", error = normalized.Error ?? "bad_room" }, ctx.RequestAborted);
                            continue;
                        }

                        // Leave previous room, if any.
                        if (roomId is not null)
                        {
                            signaling.Leave(roomId, clientId);
                        }

                        roomId = normalized.Room;
                        WebRtcRoomKind roomKind = signaling.GetRoomKind(roomId);
                        WebRtcJoinResult join = signaling.TryJoin(roomId, clientId);
                        if (!join.Ok)
                        {
                            await client.SendJsonAsync(new
                            {
                                ok = false,
                                type = "webrtc.error",
                                error = join.Error ?? "join_failed",
                                room = roomId,
                                peers = join.Peers
                            }, ctx.RequestAborted);
                            roomId = null;
                            continue;
                        }

                        await client.SendJsonAsync(new
                        {
                            ok = true,
                            type = "webrtc.joined",
                            room = roomId,
                            kind = roomKind.ToString().ToLowerInvariant(),
                            clientId,
                            peers = join.Peers
                        }, ctx.RequestAborted);

                        await BroadcastAsync(signaling, roomId, clientId, new
                        {
                            ok = true,
                            type = "webrtc.peer_joined",
                            room = roomId,
                            kind = roomKind.ToString().ToLowerInvariant(),
                            peerId = clientId,
                            peers = join.Peers
                        }, ctx.RequestAborted);

                        continue;
                    }

                    if (string.Equals(mType, "signal", StringComparison.OrdinalIgnoreCase))
                    {
                        if (roomId is null || !signaling.IsJoined(roomId, clientId))
                        {
                            await client.SendJsonAsync(new { ok = false, type = "webrtc.error", error = "not_joined" }, ctx.RequestAborted);
                            continue;
                        }

                        await BroadcastAsync(signaling, roomId, clientId, new
                        {
                            ok = true,
                            type = "webrtc.signal",
                            room = roomId,
                            from = clientId,
                            data = mData
                        }, ctx.RequestAborted);

                        continue;
                    }

                    if (string.Equals(mType, "leave", StringComparison.OrdinalIgnoreCase))
                    {
                        if (roomId is not null)
                        {
                            signaling.Leave(roomId, clientId);
                            int peersLeft = signaling.GetRoomPeerCountsSnapshot().TryGetValue(roomId, out int c) ? c : 0;
                            await BroadcastAsync(signaling, roomId, clientId, new
                            {
                                ok = true,
                                type = "webrtc.peer_left",
                                room = roomId,
                                kind = signaling.GetRoomKind(roomId).ToString().ToLowerInvariant(),
                                peerId = clientId,
                                peers = peersLeft
                            }, ctx.RequestAborted);
                            roomId = null;
                        }
                        await client.SendJsonAsync(new { ok = true, type = "webrtc.left", clientId }, ctx.RequestAborted);
                        continue;
                    }

                    await client.SendJsonAsync(new { ok = false, type = "webrtc.error", error = "unsupported_type" }, ctx.RequestAborted);
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
                    signaling.Leave(roomId, clientId);
                    int peersLeft = signaling.GetRoomPeerCountsSnapshot().TryGetValue(roomId, out int c) ? c : 0;
                    await BroadcastAsync(signaling, roomId, clientId, new
                    {
                        ok = true,
                        type = "webrtc.peer_left",
                        room = roomId,
                        kind = signaling.GetRoomKind(roomId).ToString().ToLowerInvariant(),
                        peerId = clientId,
                        peers = peersLeft
                    }, CancellationToken.None);
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
