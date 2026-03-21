using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Events;
using DataChannelDotnet.Impl;
using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Application;

/// <summary>
/// Tries direct command dispatch over a DataChannelDotNet control channel.
/// Returns <see langword="null"/> when the bridge is unavailable so callers can apply fallback routing.
/// </summary>
public interface IWebRtcDcdControlBridge
{
    /// <summary>
    /// Attempts to send one command through direct DCD control channel.
    /// </summary>
    Task<CommandAckBody?> TrySendAsync(
        RealtimeTransportRouteContext route,
        CommandRequestBody command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runtime tuning knobs for DCD control-channel bridge.
/// </summary>
public sealed class WebRtcDcdControlBridgeOptions
{
    /// <summary>
    /// Max wait before giving up channel bootstrap and allowing fallback.
    /// </summary>
    public int ConnectTimeoutMs { get; init; } = 1200;

    /// <summary>
    /// Poll interval used while exchanging signaling and waiting for ACK.
    /// </summary>
    public int PollIntervalMs { get; init; } = 30;

    /// <summary>
    /// Max signaling batch size per poll.
    /// </summary>
    public int SignalBatchLimit { get; init; } = 200;
}

/// <summary>
/// DataChannelDotNet direct command bridge for control-plane transport routing.
/// </summary>
public sealed class WebRtcDcdControlBridge : IWebRtcDcdControlBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly WebRtcCommandRelayService _commandRelay;
    private readonly IWebRtcSignalingStore _signalingStore;
    private readonly WebRtcDcdControlBridgeOptions _options;
    private readonly ConcurrentDictionary<string, DcdControlSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new direct DCD control bridge.
    /// </summary>
    public WebRtcDcdControlBridge(
        WebRtcCommandRelayService commandRelay,
        IWebRtcSignalingStore signalingStore,
        WebRtcDcdControlBridgeOptions options)
    {
        _commandRelay = commandRelay;
        _signalingStore = signalingStore;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<CommandAckBody?> TrySendAsync(
        RealtimeTransportRouteContext route,
        CommandRequestBody command,
        CancellationToken cancellationToken)
    {
        var sessionId = route.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var peer = await ResolveDirectPeerAsync(route, cancellationToken);
        if (peer is null)
        {
            return null;
        }

        var routeKey = BuildRouteKey(route, peer.PeerId);
        var session = _sessions.GetOrAdd(
            routeKey,
            _ => new DcdControlSession(
                _signalingStore,
                _options,
                sessionId,
                peer.PeerId,
                BuildControlPlanePeerId(sessionId)));

        return await session.TrySendAsync(command, cancellationToken);
    }

    /// <summary>
    /// Resolves one online peer that advertises DCD transport-engine metadata for the route.
    /// </summary>
    private async Task<WebRtcPeerStateBody?> ResolveDirectPeerAsync(
        RealtimeTransportRouteContext route,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(route.SessionId))
        {
            return null;
        }

        var peers = await _commandRelay.ListPeersAsync(route.SessionId, cancellationToken);
        return peers
            .Where(static peer => peer.IsOnline)
            .Where(peer => IsRoutePeerMatch(route, peer))
            .Where(static peer => IsDcdDirectPeer(peer.Metadata))
            .OrderByDescending(static peer => peer.LastSeenAtUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Checks whether peer endpoint metadata is compatible with route endpoint selector.
    /// </summary>
    private static bool IsRoutePeerMatch(RealtimeTransportRouteContext route, WebRtcPeerStateBody peer)
    {
        return string.IsNullOrWhiteSpace(route.EndpointId)
            || string.IsNullOrWhiteSpace(peer.EndpointId)
            || string.Equals(route.EndpointId, peer.EndpointId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether peer metadata marks this peer as DCD direct-command capable.
    /// </summary>
    private static bool IsDcdDirectPeer(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("transportEngine", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "dcd" or "datachanneldotnet";
    }

    /// <summary>
    /// Builds deterministic control-plane sender/recipient peer id.
    /// </summary>
    private static string BuildControlPlanePeerId(string sessionId)
        => $"controlplane:{sessionId}";

    /// <summary>
    /// Builds deterministic route+peer key for connection reuse.
    /// </summary>
    private static string BuildRouteKey(RealtimeTransportRouteContext route, string peerId)
        => $"{route.SessionId}:{route.EndpointId}:{peerId}";

    private sealed class DcdControlSession
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly IWebRtcSignalingStore _signalingStore;
        private readonly WebRtcDcdControlBridgeOptions _options;
        private readonly string _sessionId;
        private readonly string _remotePeerId;
        private readonly string _controlPeerId;
        private readonly ConcurrentQueue<QueuedSignalPublish> _queuedPublishes = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandAckBody>> _ackWaiters = new(StringComparer.OrdinalIgnoreCase);
        private int _afterSequence;
        private IRtcPeerConnection? _peerConnection;
        private IRtcDataChannel? _controlChannel;
        private bool _initialized;

        public DcdControlSession(
            IWebRtcSignalingStore signalingStore,
            WebRtcDcdControlBridgeOptions options,
            string sessionId,
            string remotePeerId,
            string controlPeerId)
        {
            _signalingStore = signalingStore;
            _options = options;
            _sessionId = sessionId;
            _remotePeerId = remotePeerId;
            _controlPeerId = controlPeerId;
        }

        public async Task<CommandAckBody?> TrySendAsync(CommandRequestBody command, CancellationToken cancellationToken)
        {
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                EnsureInitialized();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(command.TimeoutMs, 100)));
                var token = timeoutCts.Token;

                var connectDeadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(100, _options.ConnectTimeoutMs));
                while (!IsControlChannelOpen())
                {
                    await FlushQueuedPublishesAsync(token);
                    await PumpIncomingSignalsAsync(token);
                    if (DateTimeOffset.UtcNow >= connectDeadline)
                    {
                        return null;
                    }

                    await Task.Delay(Math.Max(10, _options.PollIntervalMs), token);
                }

                var waiter = new TaskCompletionSource<CommandAckBody>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ackWaiters[command.CommandId] = waiter;
                var sentToChannel = false;
                try
                {
                    var channel = GetOpenChannel();
                    if (channel is null)
                    {
                        _ackWaiters.TryRemove(command.CommandId, out _);
                        return null;
                    }

                    channel.Send(JsonSerializer.Serialize(BuildSignalCommand(command), JsonOptions));
                    sentToChannel = true;

                    while (true)
                    {
                        await FlushQueuedPublishesAsync(token);
                        await PumpIncomingSignalsAsync(token);
                        if (waiter.Task.IsCompleted)
                        {
                            var ack = await waiter.Task.WaitAsync(token);
                            return MarkDcdBridgeMetric(ack);
                        }

                        await Task.Delay(Math.Max(10, _options.PollIntervalMs), token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && sentToChannel)
                {
                    return MarkDcdBridgeMetric(new CommandAckBody(
                        command.CommandId,
                        CommandStatus.Timeout,
                        new ErrorInfo(
                            ErrorDomain.Transport,
                            "E_TRANSPORT_TIMEOUT",
                            "WebRTC DCD control-channel ACK timeout.",
                            true)));
                }
                finally
                {
                    _ackWaiters.TryRemove(command.CommandId, out _);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private void EnsureInitialized()
        {
            lock (_sync)
            {
                if (_initialized)
                {
                    return;
                }

                var peer = new RtcPeerConnection(new RtcPeerConfiguration());
                peer.OnLocalDescriptionSafe += (_, description) => QueueLocalDescription(description);
                peer.OnCandidateSafe += (_, candidate) => QueueLocalCandidate(candidate);
                peer.OnDataChannel += (_, channel) => TryBindControlChannel(channel);

                _peerConnection = peer;
                _controlChannel = CreateAndBindControlChannel(peer);
                peer.SetLocalDescription(RtcDescriptionType.Offer);
                _initialized = true;
            }
        }

        private IRtcDataChannel CreateAndBindControlChannel(IRtcPeerConnection peer)
        {
            var channel = peer.CreateDataChannel(new RtcCreateDataChannelArgs
            {
                Label = "hid-control",
                Protocol = RtcDataChannelProtocol.Text,
            });
            BindChannelHandlers(channel);
            return channel;
        }

        private void TryBindControlChannel(IRtcDataChannel channel)
        {
            if (!string.Equals(channel.Label, "hid-control", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_sync)
            {
                _controlChannel = channel;
            }

            BindChannelHandlers(channel);
        }

        private void BindChannelHandlers(IRtcDataChannel channel)
        {
            channel.OnTextReceivedSafe += (_, payload) => HandleChannelPayload(payload.Text);
            channel.OnClose += _ =>
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_controlChannel, channel))
                    {
                        _controlChannel = null;
                    }
                }
            };
        }

        private void HandleChannelPayload(string payload)
        {
            if (!TryParseAck(payload, out var ack))
            {
                return;
            }

            if (_ackWaiters.TryGetValue(ack.CommandId, out var waiter))
            {
                waiter.TrySetResult(ack);
            }
        }

        private async Task FlushQueuedPublishesAsync(CancellationToken cancellationToken)
        {
            while (_queuedPublishes.TryDequeue(out var queued))
            {
                _ = await _signalingStore.AppendAsync(
                    _sessionId,
                    queued.Kind,
                    _controlPeerId,
                    _remotePeerId,
                    queued.Payload,
                    queued.Mid,
                    queued.MLineIndex,
                    cancellationToken);
            }
        }

        private async Task PumpIncomingSignalsAsync(CancellationToken cancellationToken)
        {
            var signals = await _signalingStore.ListAsync(
                _sessionId,
                _controlPeerId,
                _afterSequence > 0 ? _afterSequence : null,
                Math.Clamp(_options.SignalBatchLimit, 1, 500),
                cancellationToken);

            foreach (var signal in signals.OrderBy(static x => x.Sequence))
            {
                _afterSequence = Math.Max(_afterSequence, signal.Sequence);
                if (!string.Equals(signal.SenderPeerId, _remotePeerId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                switch (signal.Kind)
                {
                    case WebRtcSignalKind.Answer:
                        if (TryParseDescriptionSdp(signal.Payload, out var answerSdp))
                        {
                            _peerConnection?.SetRemoteDescription(new RtcDescription
                            {
                                Sdp = answerSdp,
                                Type = RtcDescriptionType.Answer,
                            });
                        }

                        break;
                    case WebRtcSignalKind.Offer:
                        if (TryParseDescriptionSdp(signal.Payload, out var offerSdp))
                        {
                            _peerConnection?.SetRemoteDescription(new RtcDescription
                            {
                                Sdp = offerSdp,
                                Type = RtcDescriptionType.Offer,
                            });
                            _peerConnection?.SetLocalDescription(RtcDescriptionType.Answer);
                        }

                        break;
                    case WebRtcSignalKind.IceCandidate:
                        if (TryParseIceCandidate(signal, out var candidate, out var mid))
                        {
                            _peerConnection?.AddRemoteCandidate(new RtcCandidate
                            {
                                Content = candidate,
                                Mid = mid ?? string.Empty,
                            });
                        }

                        break;
                    case WebRtcSignalKind.Ack:
                        HandleChannelPayload(signal.Payload);
                        break;
                    case WebRtcSignalKind.Bye:
                        lock (_sync)
                        {
                            _controlChannel = null;
                        }

                        break;
                }
            }
        }

        private void QueueLocalDescription(RtcDescription description)
        {
            if (string.IsNullOrWhiteSpace(description.Sdp))
            {
                return;
            }

            var kind = description.Type switch
            {
                RtcDescriptionType.Offer => WebRtcSignalKind.Offer,
                RtcDescriptionType.Answer => WebRtcSignalKind.Answer,
                _ => (WebRtcSignalKind?)null,
            };
            if (kind is null)
            {
                return;
            }

            _queuedPublishes.Enqueue(new QueuedSignalPublish(kind.Value, description.Sdp, null, null));
        }

        private void QueueLocalCandidate(RtcCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.Content))
            {
                return;
            }

            _queuedPublishes.Enqueue(new QueuedSignalPublish(
                WebRtcSignalKind.IceCandidate,
                candidate.Content,
                string.IsNullOrWhiteSpace(candidate.Mid) ? null : candidate.Mid,
                null));
        }

        private bool IsControlChannelOpen()
        {
            lock (_sync)
            {
                return _controlChannel is { IsOpen: true, IsClosed: false };
            }
        }

        private IRtcDataChannel? GetOpenChannel()
        {
            lock (_sync)
            {
                if (_controlChannel is { IsOpen: true, IsClosed: false } channel)
                {
                    return channel;
                }
            }

            return null;
        }

        private static TransportCommandMessageBody BuildSignalCommand(CommandRequestBody command)
        {
            return new TransportCommandMessageBody(
                Kind: TransportMessageKind.Command,
                CommandId: command.CommandId,
                SessionId: command.SessionId,
                Action: command.Action,
                Args: BuildTransportHidArgs(command.Args),
                TimeoutMs: Math.Max(1000, command.TimeoutMs),
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        private static TransportHidCommandArgsBody BuildTransportHidArgs(IReadOnlyDictionary<string, object?> args)
        {
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
            => args.TryGetValue(key, out var value) ? value?.ToString() : null;

        private static int? ReadInt(IReadOnlyDictionary<string, object?> args, string key)
        {
            if (!args.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Number && jsonElement.TryGetInt32(out var jsonNumber))
                {
                    return jsonNumber;
                }

                if (jsonElement.ValueKind == JsonValueKind.String
                    && int.TryParse(jsonElement.GetString(), out var jsonStringNumber))
                {
                    return jsonStringNumber;
                }
            }

            return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }

        private static bool? ReadBool(IReadOnlyDictionary<string, object?> args, string key)
        {
            if (!args.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return jsonElement.GetBoolean();
                }

                if (jsonElement.ValueKind == JsonValueKind.String
                    && bool.TryParse(jsonElement.GetString(), out var jsonStringBool))
                {
                    return jsonStringBool;
                }
            }

            return bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }

        private static bool TryParseDescriptionSdp(string payload, out string sdp)
        {
            sdp = string.Empty;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            var trimmed = payload.Trim();
            if (!trimmed.StartsWith('{'))
            {
                sdp = trimmed;
                return true;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<SignalDescriptionPayload>(trimmed, JsonOptions);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.Sdp))
                {
                    return false;
                }

                sdp = parsed.Sdp.Trim();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryParseIceCandidate(WebRtcSignalMessageBody signal, out string candidate, out string? mid)
        {
            candidate = string.Empty;
            mid = signal.Mid;
            if (string.IsNullOrWhiteSpace(signal.Payload))
            {
                return false;
            }

            var trimmed = signal.Payload.Trim();
            if (!trimmed.StartsWith('{'))
            {
                candidate = trimmed;
                return true;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<SignalIceCandidatePayload>(trimmed, JsonOptions);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.Candidate))
                {
                    return false;
                }

                candidate = parsed.Candidate.Trim();
                if (!string.IsNullOrWhiteSpace(parsed.Mid))
                {
                    mid = parsed.Mid.Trim();
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryParseAck(string payload, out CommandAckBody ack)
        {
            ack = default!;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            try
            {
                var transportAck = JsonSerializer.Deserialize<TransportAckMessageBody>(payload, JsonOptions);
                if (transportAck is not null && transportAck.Kind == TransportMessageKind.Ack)
                {
                    ack = new CommandAckBody(
                        transportAck.CommandId,
                        transportAck.Status,
                        transportAck.Error,
                        transportAck.Metrics);
                    return true;
                }
            }
            catch (JsonException)
            {
            }

            try
            {
                var commandAck = JsonSerializer.Deserialize<CommandAckBody>(payload, JsonOptions);
                if (commandAck is not null && !string.IsNullOrWhiteSpace(commandAck.CommandId))
                {
                    ack = commandAck;
                    return true;
                }
            }
            catch (JsonException)
            {
            }

            return false;
        }

        private static CommandAckBody MarkDcdBridgeMetric(CommandAckBody ack)
        {
            var metrics = ack.Metrics is null
                ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(ack.Metrics, StringComparer.OrdinalIgnoreCase);
            metrics["transportEngineDcdBridge"] = 1d;
            return ack with { Metrics = metrics };
        }

        private sealed record QueuedSignalPublish(WebRtcSignalKind Kind, string Payload, string? Mid, int? MLineIndex);

        private sealed record SignalDescriptionPayload(string? Sdp, string? Type);

        private sealed record SignalIceCandidatePayload(string? Candidate, string? Mid = null, int? MLineIndex = null);
    }
}
