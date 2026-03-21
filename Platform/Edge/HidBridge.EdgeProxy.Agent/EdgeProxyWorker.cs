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
    private readonly SemaphoreSlim _tokenRefreshGate = new(1, 1);
    private DateTimeOffset _lastHeartbeatAtUtc = DateTimeOffset.MinValue;
    private int? _lastCommandSequence;
    private int? _lastSignalSequence;
    private string _accessToken;
    private string _refreshToken;
    private DateTimeOffset _accessTokenExpiresAtUtc = DateTimeOffset.MaxValue;
    private string _effectiveSessionId;
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
        _refreshToken = _options.TokenRefreshToken;
        _effectiveSessionId = _options.SessionId;
        _callerHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-HidBridge-UserId"] = _options.PrincipalId,
            ["X-HidBridge-PrincipalId"] = _options.PrincipalId,
            ["X-HidBridge-TenantId"] = _options.TenantId,
            ["X-HidBridge-OrganizationId"] = _options.OrganizationId,
            ["X-HidBridge-Role"] = NormalizeCallerRolesHeader(_options.OperatorRolesCsv),
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
            await RotateAccessTokenAsync(preferRefreshToken: false, stoppingToken, force: true);
        }

        await EnsureEffectiveSessionRouteAsync(stoppingToken);

        _logger.LogInformation(
            "Edge proxy started. RequestedSession={RequestedSessionId}, EffectiveSession={EffectiveSessionId}, Peer={PeerId}, Endpoint={EndpointId}, Executor={ExecutorKind}, ControlWs={ControlWsUrl}",
            _options.SessionId,
            _effectiveSessionId,
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
            SessionId: _effectiveSessionId,
            PeerId: _options.PeerId,
            EndpointId: _options.EndpointId,
            PrincipalId: _options.PrincipalId,
            Utc: now);
        var payload = JsonSerializer.Serialize(heartbeat, JsonOptions);

        await SendAsync(HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(_effectiveSessionId)}/transport/webrtc/signals",
            new WebRtcSignalPublishBody(
                Kind: WebRtcSignalKind.Heartbeat,
                SenderPeerId: _options.PeerId,
                RecipientPeerId: null,
                Payload: payload),
            cancellationToken);

        var query = $"/api/v1/sessions/{Uri.EscapeDataString(_effectiveSessionId)}/transport/webrtc/signals?recipientPeerId={Uri.EscapeDataString(_options.PeerId)}&limit=20";
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
        var query = $"/api/v1/sessions/{Uri.EscapeDataString(_effectiveSessionId)}/transport/webrtc/commands?peerId={Uri.EscapeDataString(_options.PeerId)}&limit={_options.BatchLimit}";
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
                $"/api/v1/sessions/{Uri.EscapeDataString(_effectiveSessionId)}/transport/webrtc/commands/{Uri.EscapeDataString(envelope.Command.CommandId)}/ack",
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
            $"/api/v1/sessions/{Uri.EscapeDataString(_effectiveSessionId)}/transport/media/streams",
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
            $"/api/v1/sessions/{Uri.EscapeDataString(_effectiveSessionId)}/transport/webrtc/peers/{Uri.EscapeDataString(_options.PeerId)}/online",
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
            $"/api/v1/sessions/{Uri.EscapeDataString(_effectiveSessionId)}/transport/webrtc/peers/{Uri.EscapeDataString(_options.PeerId)}/offline",
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Ensures the agent is attached to an API-known session route before transport telemetry is published.
    /// </summary>
    private async Task EnsureEffectiveSessionRouteAsync(CancellationToken cancellationToken)
    {
        // First pass: ask API to resolve/reuse/create session route with the same policy used by smoke/UI.
        var ensureBody = new SessionControlEnsureBody(
            ParticipantId: $"owner:{_options.PrincipalId}",
            RequestedBy: _options.PrincipalId,
            EndpointId: _options.EndpointId,
            Profile: SessionProfile.UltraLowLatency,
            LeaseSeconds: 30,
            Reason: "edge-proxy route ensure",
            AutoCreateSessionIfMissing: true,
            PreferLiveRelaySession: true,
            TenantId: _options.TenantId,
            OrganizationId: _options.OrganizationId,
            OperatorRoles: ParseOperatorRoles(_options.OperatorRolesCsv));

        try
        {
            using var response = await SendAsync(
                HttpMethod.Post,
                $"/api/v1/sessions/{Uri.EscapeDataString(_effectiveSessionId)}/control/ensure",
                ensureBody,
                cancellationToken,
                allowNotFound: true);

            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var ensureResult = await JsonSerializer.DeserializeAsync<SessionControlEnsureResultBody>(contentStream, JsonOptions, cancellationToken);
                if (!string.IsNullOrWhiteSpace(ensureResult?.EffectiveSessionId)
                    && !string.Equals(_effectiveSessionId, ensureResult.EffectiveSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Edge proxy session route switched by control ensure: {RequestedSessionId} -> {EffectiveSessionId}.",
                        _effectiveSessionId,
                        ensureResult.EffectiveSessionId);
                    _effectiveSessionId = ensureResult.EffectiveSessionId;
                }
            }
        }
        catch (Exception ex)
        {
            // Keep runtime alive: fallback path below creates route explicitly when needed.
            _logger.LogWarning(ex, "Control ensure failed for session {SessionId}. Falling back to explicit session existence check.", _effectiveSessionId);
        }

        await EnsureSessionExistsAsync(_effectiveSessionId, cancellationToken);
    }

    /// <summary>
    /// Verifies that session route exists in API storage and creates it when missing.
    /// </summary>
    private async Task EnsureSessionExistsAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var getResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}",
            body: null,
            cancellationToken,
            allowNotFound: true);
        if (getResponse.StatusCode != HttpStatusCode.NotFound)
        {
            return;
        }

        _logger.LogWarning("Session route {SessionId} was not found in API store. Creating route explicitly.", sessionId);
        var targetAgentId = await ResolveTargetAgentIdAsync(cancellationToken);
        var openBody = new SessionOpenBody(
            SessionId: sessionId,
            Profile: SessionProfile.UltraLowLatency,
            RequestedBy: _options.PrincipalId,
            TargetAgentId: targetAgentId,
            TargetEndpointId: _options.EndpointId,
            ShareMode: SessionRole.Owner,
            TenantId: _options.TenantId,
            OrganizationId: _options.OrganizationId,
            OperatorRoles: ParseOperatorRoles(_options.OperatorRolesCsv),
            TransportProvider: "WebRtcDataChannel");

        _ = await SendAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            openBody,
            cancellationToken);
    }

    /// <summary>
    /// Resolves target agent id for configured endpoint from inventory projection.
    /// </summary>
    private async Task<string> ResolveTargetAgentIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                "/api/v1/dashboards/inventory",
                body: null,
                cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (json.RootElement.TryGetProperty("endpoints", out var endpoints)
                && endpoints.ValueKind == JsonValueKind.Array)
            {
                foreach (var endpoint in endpoints.EnumerateArray())
                {
                    var endpointId = endpoint.TryGetProperty("endpointId", out var endpointIdElement)
                        ? endpointIdElement.GetString()
                        : null;
                    if (!string.Equals(endpointId, _options.EndpointId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var agentId = endpoint.TryGetProperty("agentId", out var agentIdElement)
                        ? agentIdElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(agentId))
                    {
                        return agentId;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve target agent id from inventory for endpoint {EndpointId}.", _options.EndpointId);
        }

        // Fallback keeps session creation unblocked; command-route validation later will reveal invalid mapping.
        return $"agent_hidbridge_uart_{_options.EndpointId}";
    }

    /// <summary>
    /// Converts CSV caller roles to immutable role list expected by API bodies.
    /// </summary>
    private static IReadOnlyList<string> ParseOperatorRoles(string csv)
        => string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>
    /// Requests a new OIDC token by username/password credentials.
    /// </summary>
    private async Task<TokenResponse> RequestPasswordTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.TokenUsername)
            || string.IsNullOrWhiteSpace(_options.TokenPassword)
            || string.IsNullOrWhiteSpace(_options.TokenClientId))
        {
            throw new InvalidOperationException("Token username/password/client_id must be configured for password grant.");
        }

        return await RequestTokenGrantAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = _options.TokenClientId,
                ["username"] = _options.TokenUsername,
                ["password"] = _options.TokenPassword,
            },
            cancellationToken);
    }

    /// <summary>
    /// Requests a refreshed OIDC token using refresh token grant.
    /// </summary>
    private async Task<TokenResponse> RequestRefreshTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_refreshToken) || string.IsNullOrWhiteSpace(_options.TokenClientId))
        {
            throw new InvalidOperationException("Refresh token and client_id must be configured for refresh grant.");
        }

        return await RequestTokenGrantAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _options.TokenClientId,
                ["refresh_token"] = _refreshToken,
            },
            cancellationToken);
    }

    /// <summary>
    /// Executes one OIDC token grant request and parses token payload.
    /// </summary>
    private async Task<TokenResponse> RequestTokenGrantAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.TokenScope))
        {
            form["scope"] = _options.TokenScope;
        }
        if (!string.IsNullOrWhiteSpace(_options.TokenClientSecret))
        {
            form["client_secret"] = _options.TokenClientSecret;
        }

        var client = _httpClientFactory.CreateClient("edge-proxy-auth");
        var endpoint = $"/realms/{_options.KeycloakRealm}/protocol/openid-connect/token";
        using var content = new FormUrlEncodedContent(form);

        using var response = await client.PostAsync(endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var token = await JsonSerializer.DeserializeAsync<TokenResponse>(stream, JsonOptions, cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("OIDC token response did not contain access_token.");
        }

        return token;
    }

    /// <summary>
    /// Applies a newly acquired access token/refresh token pair to in-memory runtime state.
    /// </summary>
    private void ApplyTokenGrant(TokenResponse token)
    {
        _accessToken = token.AccessToken;
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            _refreshToken = token.RefreshToken;
        }

        var expiresInSec = token.ExpiresIn.GetValueOrDefault() > 0 ? token.ExpiresIn!.Value : 300;
        _accessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresInSec);
    }

    /// <summary>
    /// Refreshes bearer token via refresh-token grant and falls back to password grant when needed.
    /// </summary>
    private async Task RotateAccessTokenAsync(bool preferRefreshToken, CancellationToken cancellationToken, bool force = false)
    {
        await _tokenRefreshGate.WaitAsync(cancellationToken);
        try
        {
            if (!force && !ShouldRefreshToken())
            {
                return;
            }

            TokenResponse? token = null;
            Exception? refreshError = null;

            if (preferRefreshToken && !string.IsNullOrWhiteSpace(_refreshToken))
            {
                try
                {
                    token = await RequestRefreshTokenAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    refreshError = ex;
                    _logger.LogWarning(ex, "Refresh-token grant failed. Falling back to password grant.");
                }
            }

            if (token is null)
            {
                if (!_options.AllowPasswordGrantFallback)
                {
                    if (refreshError is not null)
                    {
                        throw new InvalidOperationException("Refresh-token grant failed and password fallback is disabled.", refreshError);
                    }

                    throw new InvalidOperationException("Password grant fallback is disabled and no valid refresh-token grant result is available.");
                }

                if (string.IsNullOrWhiteSpace(_options.TokenUsername)
                    || string.IsNullOrWhiteSpace(_options.TokenPassword)
                    || string.IsNullOrWhiteSpace(_options.TokenClientId))
                {
                    if (refreshError is not null)
                    {
                        throw new InvalidOperationException("Failed to refresh bearer token and password grant is not configured.", refreshError);
                    }

                    throw new InvalidOperationException("Cannot acquire bearer token: token credentials are not configured.");
                }

                token = await RequestPasswordTokenAsync(cancellationToken);
            }

            ApplyTokenGrant(token);
        }
        finally
        {
            _tokenRefreshGate.Release();
        }
    }

    /// <summary>
    /// Determines whether access token should be refreshed before the next request.
    /// </summary>
    private bool ShouldRefreshToken()
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            return true;
        }

        if (_accessTokenExpiresAtUtc == DateTimeOffset.MaxValue)
        {
            // Externally provided static access token. Refresh only on 401 challenge.
            return false;
        }

        var refreshAtUtc = _accessTokenExpiresAtUtc.AddSeconds(-Math.Max(5, _options.TokenRefreshSkewSec));
        return DateTimeOffset.UtcNow >= refreshAtUtc;
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
        var canAcquireToken = !string.IsNullOrWhiteSpace(_options.TokenUsername)
            && !string.IsNullOrWhiteSpace(_options.TokenPassword)
            && !string.IsNullOrWhiteSpace(_options.TokenClientId);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 0 && ShouldRefreshToken() && canAcquireToken)
            {
                await RotateAccessTokenAsync(preferRefreshToken: true, cancellationToken);
            }

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
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && canAcquireToken)
            {
                response.Dispose();
                await RotateAccessTokenAsync(preferRefreshToken: true, cancellationToken, force: true);
                continue;
            }

            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            {
                return response;
            }

            response.EnsureSuccessStatusCode();
            return response;
        }

        throw new InvalidOperationException("Request retry exhausted while refreshing access token.");
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

    /// <summary>
    /// Normalizes caller roles header to a deterministic comma-separated list.
    /// </summary>
    private static string NormalizeCallerRolesHeader(string? rolesCsv)
    {
        var normalized = (rolesCsv ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static role => role.Trim())
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0
            ? "operator.edge"
            : string.Join(",", normalized);
    }

    private sealed class CollectionEnvelope<T>
    {
        public List<T>? Items { get; set; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }
    }
}
