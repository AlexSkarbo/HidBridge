using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using HidBridge.Edge.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Bridges WebRTC relay command envelopes from ControlPlane API to a local control websocket endpoint.
/// </summary>
public sealed class EdgeProxyWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEdgeCommandExecutor _commandExecutor;
    private readonly EdgeProxyOptions _options;
    private readonly ILogger<EdgeProxyWorker> _logger;
    private readonly Dictionary<string, string> _callerHeaders;
    private DateTimeOffset _lastHeartbeatAtUtc = DateTimeOffset.MinValue;
    private int? _lastCommandSequence;
    private int? _lastSignalSequence;
    private string _accessToken;
    private bool _peerOnline;

    /// <summary>
    /// Initializes a new <see cref="EdgeProxyWorker"/> instance.
    /// </summary>
    public EdgeProxyWorker(
        IHttpClientFactory httpClientFactory,
        IEdgeCommandExecutor commandExecutor,
        IOptions<EdgeProxyOptions> options,
        ILogger<EdgeProxyWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _commandExecutor = commandExecutor;
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

    /// <summary>
    /// Runs the worker loop:
    /// publish peer presence, heartbeat, relay poll, command execute, and command ACK.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            _accessToken = await RequestTokenAsync(stoppingToken);
        }

        _logger.LogInformation(
            "Edge proxy started. Session={SessionId}, Peer={PeerId}, Endpoint={EndpointId}, ControlWs={ControlWsUrl}",
            _options.SessionId,
            _options.PeerId,
            _options.EndpointId,
            _options.ControlWsUrl);

        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_peerOnline)
                {
                    await MarkPeerOnlineAsync(
                        BuildPeerMetadata(
                            state: "Connected",
                            failureReason: null,
                            consecutiveFailures: consecutiveFailures,
                            reconnectBackoffMs: null),
                        stoppingToken);
                    _peerOnline = true;
                    if (consecutiveFailures > 0)
                    {
                        _logger.LogInformation("Edge proxy recovered after {FailureCount} transient failures.", consecutiveFailures);
                    }
                }

                await PublishHeartbeatAsync(stoppingToken);
                await PollAndProcessCommandsAsync(stoppingToken);
                consecutiveFailures = 0;
                await Task.Delay(_options.PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var backoff = ComputeReconnectDelay(consecutiveFailures);

                _logger.LogWarning(
                    ex,
                    "Edge proxy transient failure #{FailureCount}. Reconnect backoff={BackoffMs}ms.",
                    consecutiveFailures,
                    (int)backoff.TotalMilliseconds);

                if (_peerOnline)
                {
                    try
                    {
                        await MarkPeerOnlineAsync(
                            BuildPeerMetadata(
                                state: "Degraded",
                                failureReason: $"{ex.GetType().Name}: {ex.Message}",
                                consecutiveFailures: consecutiveFailures,
                                reconnectBackoffMs: (int)backoff.TotalMilliseconds),
                            stoppingToken);
                    }
                    catch (Exception degradedEx)
                    {
                        _logger.LogDebug(degradedEx, "Failed to publish degraded peer metadata.");
                    }
                }

                if (_peerOnline && consecutiveFailures >= _options.TransientFailureThresholdForOffline)
                {
                    try
                    {
                        await MarkPeerOfflineAsync(stoppingToken);
                        _peerOnline = false;
                        _logger.LogWarning("Edge proxy marked peer offline after {FailureCount} transient failures.", consecutiveFailures);
                    }
                    catch (Exception offlineEx)
                    {
                        _logger.LogWarning(offlineEx, "Failed to mark peer offline after transient failures.");
                    }
                }

                await Task.Delay(backoff, stoppingToken);
            }
        }

        if (_peerOnline)
        {
            try
            {
                await MarkPeerOfflineAsync(CancellationToken.None);
                _peerOnline = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark peer offline.");
            }
        }
    }

    /// <summary>
    /// Computes an exponential reconnect backoff with bounded jitter.
    /// </summary>
    private TimeSpan ComputeReconnectDelay(int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 12);
        var baseDelay = _options.ReconnectBackoffMinMs * Math.Pow(2, exponent);
        var capped = Math.Min(baseDelay, _options.ReconnectBackoffMaxMs);
        var jitter = _options.ReconnectBackoffJitterMs > 0
            ? Random.Shared.Next(0, _options.ReconnectBackoffJitterMs + 1)
            : 0;
        return TimeSpan.FromMilliseconds(capped + jitter);
    }

    /// <summary>
    /// Publishes heartbeat signal packets and advances inbound signal cursor.
    /// </summary>
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

    /// <summary>
    /// Polls command envelopes, executes each command, and posts ACK payloads back to relay queue.
    /// </summary>
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
            using var ackResponse = await SendAsync(HttpMethod.Post,
                $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/commands/{Uri.EscapeDataString(envelope.Command.CommandId)}/ack",
                ack,
                cancellationToken,
                allowNotFound: true);
            if (ackResponse.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "Relay ACK target was not found for command {CommandId}; it was likely expired or already acknowledged.",
                    envelope.Command.CommandId);
            }

            _lastCommandSequence = envelope.Sequence;
        }
    }

    /// <summary>
    /// Executes one relay command through injected edge command executor and shapes relay ACK payload.
    /// </summary>
    private async Task<object> ExecuteCommandAsync(CommandRequestBody command, CancellationToken cancellationToken)
    {
        var effectiveTimeout = Math.Max(1000, command.TimeoutMs ?? _options.CommandTimeoutMs);

        try
        {
            var edgeRequest = new EdgeCommandRequest(
                CommandId: command.CommandId,
                Action: command.Action,
                Args: command.Args,
                TimeoutMs: effectiveTimeout);
            var execution = await _commandExecutor.ExecuteAsync(edgeRequest, cancellationToken);

            var metrics = new Dictionary<string, double>
            {
                ["relayAdapterWsRoundtripMs"] = execution.RoundtripMs,
                ["transportRelayMode"] = 1,
            };

            if (execution.IsSuccess)
            {
                return new
                {
                    status = "Applied",
                    metrics,
                };
            }

            if (execution.IsTimeout)
            {
                return new
                {
                    status = "Timeout",
                    error = new
                    {
                        domain = "Transport",
                        code = string.IsNullOrWhiteSpace(execution.ErrorCode) ? "E_TRANSPORT_TIMEOUT" : execution.ErrorCode,
                        message = string.IsNullOrWhiteSpace(execution.ErrorMessage) ? $"WebRTC peer timeout for action '{command.Action}'." : execution.ErrorMessage,
                        retryable = true,
                        details = new Dictionary<string, object?>
                        {
                            ["peerId"] = _options.PeerId,
                            ["controlWsUrl"] = _options.ControlWsUrl,
                            ["ackSource"] = "edge-proxy-agent",
                        },
                    },
                    metrics,
                };
            }

            return new
            {
                status = "Rejected",
                error = new
                {
                    domain = "Command",
                    code = string.IsNullOrWhiteSpace(execution.ErrorCode) ? "E_WEBRTC_PEER_EXECUTION_FAILED" : execution.ErrorCode,
                    message = string.IsNullOrWhiteSpace(execution.ErrorMessage) ? "Peer returned non-success response." : execution.ErrorMessage,
                    retryable = false,
                    details = new Dictionary<string, object?>
                    {
                        ["peerId"] = _options.PeerId,
                        ["controlWsUrl"] = _options.ControlWsUrl,
                        ["ackSource"] = "edge-proxy-agent",
                    },
                },
                metrics,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new
            {
                status = "Timeout",
                error = new
                {
                    domain = "Transport",
                    code = "E_TRANSPORT_TIMEOUT",
                    message = $"WebRTC peer timeout for action '{command.Action}'.",
                    retryable = true,
                    details = new Dictionary<string, object?>
                    {
                        ["peerId"] = _options.PeerId,
                        ["controlWsUrl"] = _options.ControlWsUrl,
                        ["ackSource"] = "edge-proxy-agent",
                    },
                },
                metrics = new Dictionary<string, double>
                {
                    ["relayAdapterWsRoundtripMs"] = effectiveTimeout,
                    ["transportRelayMode"] = 1,
                },
            };
        }
        catch (Exception ex)
        {
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
                    details = new Dictionary<string, object?>
                    {
                        ["peerId"] = _options.PeerId,
                        ["controlWsUrl"] = _options.ControlWsUrl,
                        ["ackSource"] = "edge-proxy-agent",
                    },
                },
                metrics = new Dictionary<string, double>
                {
                    ["relayAdapterWsRoundtripMs"] = effectiveTimeout,
                    ["transportRelayMode"] = 1,
                },
            };
        }
    }

    /// <summary>
    /// Convenience overload that publishes online peer state without extra metadata.
    /// </summary>
    private async Task MarkPeerOnlineAsync(CancellationToken cancellationToken)
        => await MarkPeerOnlineAsync(metadata: null, cancellationToken);

    /// <summary>
    /// Publishes online peer state with optional metadata extensions.
    /// </summary>
    private async Task MarkPeerOnlineAsync(
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        var effectiveMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["adapter"] = "edge-proxy-agent",
            ["controlWsUrl"] = _options.ControlWsUrl,
            ["principalId"] = _options.PrincipalId,
        };

        if (metadata is not null)
        {
            foreach (var pair in metadata)
            {
                effectiveMetadata[pair.Key] = pair.Value;
            }
        }

        await SendAsync(HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/peers/{Uri.EscapeDataString(_options.PeerId)}/online",
            new
            {
                endpointId = _options.EndpointId,
                metadata = effectiveMetadata,
            },
            cancellationToken);
    }

    /// <summary>
    /// Publishes offline peer state.
    /// </summary>
    private async Task MarkPeerOfflineAsync(CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/peers/{Uri.EscapeDataString(_options.PeerId)}/offline",
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Requests OIDC access token for API calls that require bearer authorization.
    /// </summary>
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

    /// <summary>
    /// Parses collection responses from direct array or envelope shapes used across runtime endpoints.
    /// </summary>
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

    /// <summary>
    /// Sends one API call with caller headers and bearer token refresh on 401.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken cancellationToken,
        bool allowNotFound = false)
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
            return await SendAsync(method, relativeUrl, body, cancellationToken, allowNotFound);
        }

        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return response;
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>
    /// Builds peer metadata payload used for online/degraded state transitions.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildPeerMetadata(
        string state,
        string? failureReason,
        int consecutiveFailures,
        int? reconnectBackoffMs)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["state"] = state,
            ["consecutiveFailures"] = consecutiveFailures.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        if (!string.IsNullOrWhiteSpace(failureReason))
        {
            metadata["failureReason"] = failureReason;
        }
        if (reconnectBackoffMs.HasValue)
        {
            metadata["reconnectBackoffMs"] = reconnectBackoffMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return metadata;
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
