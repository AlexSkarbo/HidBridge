using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using HidBridge.Edge.Abstractions;
using HidBridge.Contracts;
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
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHidBridgeHidAdapter _hidAdapter;
    private readonly ICaptureAdapter _captureAdapter;
    private readonly EdgeProxyOptions _options;
    private readonly ILogger<EdgeProxyWorker> _logger;
    private readonly Dictionary<string, string> _callerHeaders;
    private DateTimeOffset _lastHeartbeatAtUtc = DateTimeOffset.MinValue;
    private int? _lastCommandSequence;
    private int? _lastSignalSequence;
    private string _accessToken;
    private bool _peerOnline;
    private EdgeProxyLifecycleState _lifecycleState = EdgeProxyLifecycleState.Starting;
    private DateTimeOffset _lifecycleStateChangedAtUtc = DateTimeOffset.UtcNow;
    private string? _lifecycleStateReason;

    /// <summary>
    /// Initializes a new <see cref="EdgeProxyWorker"/> instance.
    /// </summary>
    public EdgeProxyWorker(
        IHttpClientFactory httpClientFactory,
        IHidBridgeHidAdapter hidAdapter,
        ICaptureAdapter captureAdapter,
        IOptions<EdgeProxyOptions> options,
        ILogger<EdgeProxyWorker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _hidAdapter = hidAdapter;
        _captureAdapter = captureAdapter;
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
            "Edge proxy started. Session={SessionId}, Peer={PeerId}, Endpoint={EndpointId}, Executor={ExecutorKind}, ControlWs={ControlWsUrl}",
            _options.SessionId,
            _options.PeerId,
            _options.EndpointId,
            _options.GetCommandExecutorKind(),
            _options.ControlWsUrl);

        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_peerOnline)
                {
                    TransitionLifecycleState(EdgeProxyLifecycleState.Connecting, "opening relay peer session");
                    await RefreshPeerOnlineMetadataAsync(
                        state: EdgeProxyLifecycleState.Connected,
                        failureReason: null,
                        consecutiveFailures: consecutiveFailures,
                        reconnectBackoffMs: null,
                        stoppingToken);
                    _peerOnline = true;
                    TransitionLifecycleState(EdgeProxyLifecycleState.Connected, null);
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
                    TransitionLifecycleState(EdgeProxyLifecycleState.Degraded, $"{ex.GetType().Name}: {ex.Message}");
                    try
                    {
                        await RefreshPeerOnlineMetadataAsync(
                            state: EdgeProxyLifecycleState.Degraded,
                            failureReason: $"{ex.GetType().Name}: {ex.Message}",
                            consecutiveFailures: consecutiveFailures,
                            reconnectBackoffMs: (int)backoff.TotalMilliseconds,
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
                        TransitionLifecycleState(EdgeProxyLifecycleState.Offline, "transient failure threshold reached");
                        await MarkPeerOfflineAsync(stoppingToken);
                        _peerOnline = false;
                        _logger.LogWarning("Edge proxy marked peer offline after {FailureCount} transient failures.", consecutiveFailures);
                    }
                    catch (Exception offlineEx)
                    {
                        _logger.LogWarning(offlineEx, "Failed to mark peer offline after transient failures.");
                    }
                }

                TransitionLifecycleState(EdgeProxyLifecycleState.Reconnecting, $"retry in {(int)backoff.TotalMilliseconds}ms");
                await Task.Delay(backoff, stoppingToken);
            }
        }

        if (_peerOnline)
        {
            try
            {
                TransitionLifecycleState(EdgeProxyLifecycleState.Offline, "runtime stop requested");
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

        // Refresh peer metadata on each heartbeat tick so readiness projections include up-to-date media state.
        await RefreshPeerOnlineMetadataAsync(
            state: EdgeProxyLifecycleState.Connected,
            failureReason: null,
            consecutiveFailures: 0,
            reconnectBackoffMs: null,
            cancellationToken);

        var heartbeat = new TransportHeartbeatMessageBody(
            Kind: TransportMessageKind.Heartbeat,
            SessionId: _options.SessionId,
            PeerId: _options.PeerId,
            EndpointId: _options.EndpointId,
            PrincipalId: _options.PrincipalId,
            Utc: now);
        var payload = JsonSerializer.Serialize(heartbeat, JsonOptions);

        await SendAsync(HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/signals",
            new WebRtcSignalPublishBody(
                Kind: WebRtcSignalKind.Heartbeat,
                SenderPeerId: _options.PeerId,
                RecipientPeerId: null,
                Payload: payload),
            cancellationToken);

        var query = $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/webrtc/signals?recipientPeerId={Uri.EscapeDataString(_options.PeerId)}&limit=20";
        if (_lastSignalSequence.HasValue)
        {
            query += $"&afterSequence={_lastSignalSequence.Value}";
        }

        var signals = await GetCollectionAsync<WebRtcSignalMessageBody>(query, cancellationToken);
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

        var envelopes = await GetCollectionAsync<WebRtcCommandEnvelopeBody>(query, cancellationToken);
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
    private async Task<TransportAckMessageBody> ExecuteCommandAsync(CommandRequestBody command, CancellationToken cancellationToken)
    {
        var effectiveTimeout = Math.Max(1000, command.TimeoutMs);
        var transportCommand = new TransportCommandMessageBody(
            Kind: TransportMessageKind.Command,
            CommandId: command.CommandId,
            SessionId: command.SessionId,
            Action: command.Action,
            Args: BuildTypedHidArgs(command.Args),
            TimeoutMs: effectiveTimeout,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        try
        {
            var execution = await _hidAdapter.ExecuteAsync(transportCommand, cancellationToken);

            var metrics = new Dictionary<string, double>
            {
                ["relayAdapterWsRoundtripMs"] = execution.RoundtripMs,
                ["transportRelayMode"] = 1,
            };

            if (execution.IsSuccess)
            {
                return new TransportAckMessageBody(
                    Kind: TransportMessageKind.Ack,
                    CommandId: command.CommandId,
                    Status: CommandStatus.Applied,
                    AcknowledgedAtUtc: DateTimeOffset.UtcNow,
                    Metrics: metrics);
            }

            if (execution.IsTimeout)
            {
                return new TransportAckMessageBody(
                    Kind: TransportMessageKind.Ack,
                    CommandId: command.CommandId,
                    Status: CommandStatus.Timeout,
                    AcknowledgedAtUtc: DateTimeOffset.UtcNow,
                    Error: new ErrorInfo(
                        Domain: ErrorDomain.Transport,
                        Code: string.IsNullOrWhiteSpace(execution.ErrorCode) ? "E_TRANSPORT_TIMEOUT" : execution.ErrorCode,
                        Message: string.IsNullOrWhiteSpace(execution.ErrorMessage) ? $"WebRTC peer timeout for action '{command.Action}'." : execution.ErrorMessage,
                        Retryable: true,
                        Details: new Dictionary<string, object?>
                        {
                            ["peerId"] = _options.PeerId,
                            ["controlWsUrl"] = _options.ControlWsUrl,
                            ["ackSource"] = "edge-proxy-agent",
                        }),
                    Metrics: metrics);
            }

            return new TransportAckMessageBody(
                Kind: TransportMessageKind.Ack,
                CommandId: command.CommandId,
                Status: CommandStatus.Rejected,
                AcknowledgedAtUtc: DateTimeOffset.UtcNow,
                Error: new ErrorInfo(
                    Domain: ErrorDomain.Command,
                    Code: string.IsNullOrWhiteSpace(execution.ErrorCode) ? "E_WEBRTC_PEER_EXECUTION_FAILED" : execution.ErrorCode,
                    Message: string.IsNullOrWhiteSpace(execution.ErrorMessage) ? "Peer returned non-success response." : execution.ErrorMessage,
                    Retryable: false,
                    Details: new Dictionary<string, object?>
                    {
                        ["peerId"] = _options.PeerId,
                        ["controlWsUrl"] = _options.ControlWsUrl,
                        ["ackSource"] = "edge-proxy-agent",
                    }),
                Metrics: metrics);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TransportAckMessageBody(
                Kind: TransportMessageKind.Ack,
                CommandId: command.CommandId,
                Status: CommandStatus.Timeout,
                AcknowledgedAtUtc: DateTimeOffset.UtcNow,
                Error: new ErrorInfo(
                    Domain: ErrorDomain.Transport,
                    Code: "E_TRANSPORT_TIMEOUT",
                    Message: $"WebRTC peer timeout for action '{command.Action}'.",
                    Retryable: true,
                    Details: new Dictionary<string, object?>
                    {
                        ["peerId"] = _options.PeerId,
                        ["controlWsUrl"] = _options.ControlWsUrl,
                        ["ackSource"] = "edge-proxy-agent",
                    }),
                Metrics: new Dictionary<string, double>
                {
                    ["relayAdapterWsRoundtripMs"] = effectiveTimeout,
                    ["transportRelayMode"] = 1,
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Command failed in edge proxy adapter. CommandId={CommandId}", command.CommandId);
            return new TransportAckMessageBody(
                Kind: TransportMessageKind.Ack,
                CommandId: command.CommandId,
                Status: CommandStatus.Rejected,
                AcknowledgedAtUtc: DateTimeOffset.UtcNow,
                Error: new ErrorInfo(
                    Domain: ErrorDomain.Command,
                    Code: "E_WEBRTC_PEER_ADAPTER_FAILURE",
                    Message: ex.Message,
                    Retryable: false,
                    Details: new Dictionary<string, object?>
                    {
                        ["peerId"] = _options.PeerId,
                        ["controlWsUrl"] = _options.ControlWsUrl,
                        ["ackSource"] = "edge-proxy-agent",
                    }),
                Metrics: new Dictionary<string, double>
                {
                    ["relayAdapterWsRoundtripMs"] = effectiveTimeout,
                    ["transportRelayMode"] = 1,
                });
        }
    }

    /// <summary>
    /// Recomputes peer metadata (transport + media readiness) and publishes online snapshot.
    /// </summary>
    private async Task RefreshPeerOnlineMetadataAsync(
        EdgeProxyLifecycleState state,
        string? failureReason,
        int consecutiveFailures,
        int? reconnectBackoffMs,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>(
            BuildPeerMetadata(
                state,
                failureReason,
                consecutiveFailures,
                reconnectBackoffMs,
                _lifecycleStateChangedAtUtc),
            StringComparer.OrdinalIgnoreCase);

        var mediaSnapshot = await ReadMediaSnapshotSafeAsync(cancellationToken);
        await PublishMediaStreamSnapshotAsync(mediaSnapshot, cancellationToken);
        foreach (var pair in BuildMediaMetadata(mediaSnapshot))
        {
            metadata[pair.Key] = pair.Value;
        }

        await MarkPeerOnlineAsync(metadata, cancellationToken);
    }

    /// <summary>
    /// Updates canonical lifecycle state and state-change timestamp.
    /// </summary>
    private void TransitionLifecycleState(EdgeProxyLifecycleState nextState, string? reason)
    {
        if (_lifecycleState == nextState && string.Equals(_lifecycleStateReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        _lifecycleState = nextState;
        _lifecycleStateReason = reason;
        _lifecycleStateChangedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Reads media readiness from probe and converts probe failures into deterministic degraded snapshots.
    /// </summary>
    private async Task<EdgeMediaReadinessSnapshot> ReadMediaSnapshotSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _captureAdapter.GetReadinessAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read media readiness snapshot.");
            return new EdgeMediaReadinessSnapshot(
                IsReady: false,
                State: "ProbeFailed",
                ReportedAtUtc: DateTimeOffset.UtcNow,
                FailureReason: ex.Message,
                StreamId: _options.MediaStreamId,
                Source: _options.MediaSource,
                Metrics: new Dictionary<string, object?>
                {
                    ["probeErrorType"] = ex.GetType().Name,
                });
        }
    }

    /// <summary>
    /// Projects dynamic command arguments to typed transport HID arguments.
    /// </summary>
    private static TransportHidCommandArgsBody BuildTypedHidArgs(IReadOnlyDictionary<string, object?>? args)
    {
        args ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        return new TransportHidCommandArgsBody(
            Text: ReadString(args, "text"),
            Shortcut: ReadString(args, "shortcut"),
            Usage: ReadInt(args, "usage"),
            Modifiers: ReadInt(args, "modifiers"),
            Dx: ReadInt(args, "dx"),
            Dy: ReadInt(args, "dy"),
            Wheel: ReadInt(args, "wheel"),
            Delta: ReadInt(args, "delta"),
            Button: ReadString(args, "button"),
            Down: ReadBool(args, "down"),
            HoldMs: ReadInt(args, "holdMs"),
            InterfaceSelector: ReadInt(args, "itfSel"));
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
        => args.TryGetValue(key, out var value)
            ? value switch
            {
                null => null,
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
                _ => value.ToString(),
            }
            : null;

    private static int? ReadInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int number => number,
            long number => checked((int)number),
            byte number => number,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } json when int.TryParse(json.GetString(), out var number) => number,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } json when bool.TryParse(json.GetString(), out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt32(out var number) => number != 0,
            _ when bool.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Projects media snapshot fields into peer metadata consumed by relay health/readiness services.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildMediaMetadata(EdgeMediaReadinessSnapshot snapshot)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mediaReady"] = snapshot.IsReady ? "true" : "false",
            ["mediaState"] = string.IsNullOrWhiteSpace(snapshot.State) ? "Unknown" : snapshot.State,
            ["mediaReportedAtUtc"] = snapshot.ReportedAtUtc.ToString("O"),
        };

        if (!string.IsNullOrWhiteSpace(snapshot.FailureReason))
        {
            metadata["mediaFailureReason"] = snapshot.FailureReason;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.StreamId))
        {
            metadata["mediaStreamId"] = snapshot.StreamId;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Source))
        {
            metadata["mediaSource"] = snapshot.Source;
        }

        return metadata;
    }

    /// <summary>
    /// Publishes media stream snapshot into the platform media layer for stream-level diagnostics.
    /// </summary>
    private async Task PublishMediaStreamSnapshotAsync(EdgeMediaReadinessSnapshot snapshot, CancellationToken cancellationToken)
    {
        var effectiveStreamId = string.IsNullOrWhiteSpace(snapshot.StreamId)
            ? _options.MediaStreamId
            : snapshot.StreamId;
        var effectiveSource = string.IsNullOrWhiteSpace(snapshot.Source)
            ? _options.MediaSource
            : snapshot.Source;

        await SendAsync(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(_options.SessionId)}/transport/media/streams",
            new
            {
                peerId = _options.PeerId,
                endpointId = _options.EndpointId,
                streamId = effectiveStreamId,
                ready = snapshot.IsReady,
                state = snapshot.State,
                reportedAtUtc = snapshot.ReportedAtUtc,
                failureReason = snapshot.FailureReason,
                source = effectiveSource,
                metrics = snapshot.Metrics,
            },
            cancellationToken);
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
            new WebRtcPeerPresenceBody(
                EndpointId: _options.EndpointId,
                Metadata: effectiveMetadata),
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
        EdgeProxyLifecycleState state,
        string? failureReason,
        int consecutiveFailures,
        int? reconnectBackoffMs,
        DateTimeOffset stateChangedAtUtc)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["state"] = state.ToString(),
            ["stateReason"] = string.IsNullOrWhiteSpace(failureReason) ? "none" : failureReason,
            ["stateChangedAtUtc"] = stateChangedAtUtc.ToString("O"),
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
}
