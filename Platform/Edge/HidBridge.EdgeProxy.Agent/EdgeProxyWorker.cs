using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HidBridge.EdgeProxy.Agent;

public sealed class EdgeProxyWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EdgeProxyOptions _options;
    private readonly ILogger<EdgeProxyWorker> _logger;
    private readonly Dictionary<string, string> _callerHeaders;
    private DateTimeOffset _lastHeartbeatAtUtc = DateTimeOffset.MinValue;
    private int? _lastCommandSequence;
    private int? _lastSignalSequence;
    private string _accessToken;

    public EdgeProxyWorker(
        IHttpClientFactory httpClientFactory,
        IOptions<EdgeProxyOptions> options,
        ILogger<EdgeProxyWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _accessToken = _options.AccessToken;
        _callerHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-HidBridge-UserId"] = _options.PrincipalId,
            ["X-HidBridge-PrincipalId"] = _options.PrincipalId,
            ["X-HidBridge-TenantId"] = _options.TenantId,
            ["X-HidBridge-OrganizationId"] = _options.OrganizationId,
            ["X-HidBridge-Role"] = "operator.admin,operator.moderator,operator.viewer",
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            _accessToken = await RequestTokenAsync(stoppingToken);
        }

        await MarkPeerOnlineAsync(stoppingToken);

        _logger.LogInformation("Edge proxy started. Session={SessionId}, Peer={PeerId}, Endpoint={EndpointId}", _options.SessionId, _options.PeerId, _options.EndpointId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PublishHeartbeatAsync(stoppingToken);
                await PollAndProcessCommandsAsync(stoppingToken);
                await Task.Delay(_options.PollIntervalMs, stoppingToken);
            }
        }
        finally
        {
            try
            {
                await MarkPeerOfflineAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark peer offline.");
            }
        }
    }

    private async Task PublishHeartbeatAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastHeartbeatAtUtc).TotalSeconds < _options.HeartbeatIntervalSec)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            utc = now,
            adapter = "edge-proxy-agent",
            peerId = _options.PeerId,
            endpointId = _options.EndpointId,
            principalId = _options.PrincipalId,
        });

        await SendAsync(HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/signals",
            new
            {
                kind = "Heartbeat",
                senderPeerId = _options.PeerId,
                payload,
            },
            cancellationToken);

        var query = $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/signals?recipientPeerId={Uri.EscapeDataString(_options.PeerId)}&limit=20";
        if (_lastSignalSequence.HasValue)
        {
            query += $"&afterSequence={_lastSignalSequence.Value}";
        }

        var signals = await GetCollectionAsync<WebRtcSignalEnvelope>(query, cancellationToken);
        foreach (var signal in signals)
        {
            _lastSignalSequence = signal.Sequence;
        }

        _lastHeartbeatAtUtc = now;
    }

    private async Task PollAndProcessCommandsAsync(CancellationToken cancellationToken)
    {
        var query = $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/commands?peerId={Uri.EscapeDataString(_options.PeerId)}&limit={_options.BatchLimit}";
        if (_lastCommandSequence.HasValue)
        {
            query += $"&afterSequence={_lastCommandSequence.Value}";
        }

        var envelopes = await GetCollectionAsync<WebRtcCommandEnvelope>(query, cancellationToken);
        foreach (var envelope in envelopes.OrderBy(x => x.Sequence))
        {
            if (envelope.Command is null || string.IsNullOrWhiteSpace(envelope.Command.CommandId))
            {
                _lastCommandSequence = envelope.Sequence;
                continue;
            }

            var ack = await ExecuteCommandAsync(envelope.Command, cancellationToken);
            await SendAsync(HttpMethod.Post,
                $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/commands/{Uri.EscapeDataString(envelope.Command.CommandId)}/ack",
                ack,
                cancellationToken);

            _lastCommandSequence = envelope.Sequence;
        }
    }

    private async Task<object> ExecuteCommandAsync(CommandRequestBody command, CancellationToken cancellationToken)
    {
        var effectiveTimeout = Math.Max(1000, command.TimeoutMs ?? _options.CommandTimeoutMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var payload = ConvertPayload(command);
            var exec = await InvokeControlWsAsync(payload, effectiveTimeout, cancellationToken);
            sw.Stop();

            var metrics = new Dictionary<string, double>
            {
                ["relayAdapterWsRoundtripMs"] = sw.Elapsed.TotalMilliseconds,
                ["transportRelayMode"] = 1,
            };

            if (exec.Ok)
            {
                return new
                {
                    status = "Applied",
                    metrics,
                };
            }

            return new
            {
                status = "Rejected",
                error = new
                {
                    domain = "Command",
                    code = "E_WEBRTC_PEER_EXECUTION_FAILED",
                    message = string.IsNullOrWhiteSpace(exec.Error) ? "Peer returned non-success response." : exec.Error,
                    retryable = false,
                },
                metrics,
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new
            {
                status = "Timeout",
                error = new
                {
                    domain = "Transport",
                    code = "E_TRANSPORT_TIMEOUT",
                    message = $"WebRTC peer timeout for action '{command.Action}'.",
                    retryable = true,
                },
                metrics = new Dictionary<string, double>
                {
                    ["relayAdapterWsRoundtripMs"] = sw.Elapsed.TotalMilliseconds,
                    ["transportRelayMode"] = 1,
                },
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Command failed in edge proxy adapter. CommandId={CommandId}", command.CommandId);
            return new
            {
                status = "Rejected",
                error = new
                {
                    domain = "Command",
                    code = "E_WEBRTC_PEER_ADAPTER_FAILURE",
                    message = ex.Message,
                    retryable = false,
                },
                metrics = new Dictionary<string, double>
                {
                    ["relayAdapterWsRoundtripMs"] = sw.Elapsed.TotalMilliseconds,
                    ["transportRelayMode"] = 1,
                },
            };
        }
    }

    private static Dictionary<string, object?> ConvertPayload(CommandRequestBody command)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = command.CommandId,
            ["type"] = command.Action,
        };

        foreach (var pair in command.Args)
        {
            if (!payload.ContainsKey(pair.Key))
            {
                payload[pair.Key] = pair.Value;
            }
        }

        return payload;
    }

    private async Task<(bool Ok, string Error)> InvokeControlWsAsync(
        Dictionary<string, object?> payload,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Math.Max(1000, timeoutMs));

        await socket.ConnectAsync(new Uri(_options.ControlWsUrl), cts.Token);

        var sendJson = JsonSerializer.Serialize(payload, JsonOptions);
        var sendBytes = Encoding.UTF8.GetBytes(sendJson);
        await socket.SendAsync(sendBytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token);

        var buffer = new byte[16 * 1024];
        var expectedId = payload.TryGetValue("id", out var idObj) ? idObj?.ToString() : null;

        while (!cts.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult receive;
            do
            {
                receive = await socket.ReceiveAsync(buffer, cts.Token);
                if (receive.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("Control websocket closed before ACK.");
                }

                ms.Write(buffer, 0, receive.Count);
            } while (!receive.EndOfMessage);

            var raw = Encoding.UTF8.GetString(ms.ToArray());
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (!TryFindAck(root, expectedId, out var okElement, out var error))
            {
                continue;
            }

            if (okElement.HasValue && okElement.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return (okElement.Value.GetBoolean(), error);
            }

            if (okElement.HasValue && okElement.Value.ValueKind == JsonValueKind.String)
            {
                var token = okElement.Value.GetString();
                if (bool.TryParse(token, out var parsed))
                {
                    return (parsed, error);
                }
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                return (false, error);
            }
        }

        throw new OperationCanceledException("Control websocket ACK timeout.");
    }

    private static bool TryFindAck(JsonElement element, string? expectedId, out JsonElement? okElement, out string error)
    {
        okElement = null;
        error = string.Empty;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAck(item, expectedId, out okElement, out error))
                {
                    return true;
                }
            }
            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var id = element.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
        var idMatches = string.IsNullOrWhiteSpace(expectedId) || string.IsNullOrWhiteSpace(id) || string.Equals(expectedId, id, StringComparison.OrdinalIgnoreCase);

        if (idMatches && element.TryGetProperty("ok", out var okProperty))
        {
            okElement = okProperty;
            if (element.TryGetProperty("error", out var errorProperty) && errorProperty.ValueKind == JsonValueKind.String)
            {
                error = errorProperty.GetString() ?? string.Empty;
            }

            return true;
        }

        foreach (var propertyName in new[] { "result", "payload", "data", "ack", "response", "message" })
        {
            if (element.TryGetProperty(propertyName, out var nested))
            {
                if (TryFindAck(nested, expectedId, out okElement, out error))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task MarkPeerOnlineAsync(CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/peers/{Uri.EscapeDataString(_options.PeerId)}/online",
            new
            {
                endpointId = _options.EndpointId,
                metadata = new
                {
                    adapter = "edge-proxy-agent",
                    controlWsUrl = _options.ControlWsUrl,
                    principalId = _options.PrincipalId,
                },
            },
            cancellationToken);
    }

    private async Task MarkPeerOfflineAsync(CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/peers/{Uri.EscapeDataString(_options.PeerId)}/offline",
            body: null,
            cancellationToken);
    }

    private async Task<string> RequestTokenAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(_options.KeycloakBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };

        var endpoint = $"/realms/{_options.KeycloakRealm}/protocol/openid-connect/token";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _options.TokenClientId,
            ["username"] = _options.TokenUsername,
            ["password"] = _options.TokenPassword,
        });

        using var response = await client.PostAsync(endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var token = await JsonSerializer.DeserializeAsync<TokenResponse>(stream, JsonOptions, cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("OIDC token response did not contain access_token.");
        }

        return token.AccessToken;
    }

    private async Task<List<T>> GetCollectionAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, relativeUrl, body: null, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<CollectionEnvelope<T>>(text, JsonOptions);
            if (envelope?.Items is { Count: > 0 })
            {
                return envelope.Items;
            }
        }
        catch
        {
        }

        try
        {
            var direct = JsonSerializer.Deserialize<List<T>>(text, JsonOptions);
            if (direct is { Count: > 0 })
            {
                return direct;
            }
        }
        catch
        {
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("items", out var itemsElement) &&
                itemsElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<T>();
                foreach (var item in itemsElement.EnumerateArray())
                {
                    var parsed = item.Deserialize<T>(JsonOptions);
                    if (parsed is not null)
                    {
                        list.Add(parsed);
                    }
                }

                return list;
            }
        }
        catch
        {
        }

        return [];
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUrl, object? body, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("edge-proxy");
        using var request = new HttpRequestMessage(method, relativeUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        foreach (var header in _callerHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (!string.IsNullOrWhiteSpace(_accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(_options.TokenUsername))
        {
            _accessToken = await RequestTokenAsync(cancellationToken);
            response.Dispose();
            return await SendAsync(method, relativeUrl, body, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    private sealed class CollectionEnvelope<T>
    {
        public List<T>? Items { get; set; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;
    }

    private sealed class WebRtcSignalEnvelope
    {
        public int Sequence { get; set; }
    }

    private sealed class WebRtcCommandEnvelope
    {
        public int Sequence { get; set; }
        public CommandRequestBody? Command { get; set; }
    }

    private sealed class CommandRequestBody
    {
        public string CommandId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public int? TimeoutMs { get; set; }
        public Dictionary<string, object?> Args { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
