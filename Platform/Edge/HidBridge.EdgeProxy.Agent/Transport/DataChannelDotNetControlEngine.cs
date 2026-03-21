using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using HidBridge.Contracts;
using Microsoft.Extensions.Logging;

namespace HidBridge.EdgeProxy.Agent.Transport;

/// <summary>
/// DataChannelDotNet control engine.
/// Uses WebRTC signaling inbox to establish peer connection and process command payloads over data channel.
/// Retains direct-signal command compatibility and optional relay fallback for safety.
/// </summary>
internal sealed class DataChannelDotNetControlEngine : IEdgeControlTransportEngine
{
    private const string ControlChannelLabel = "hid-control";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _stateGate = new();
    private readonly ILogger _logger;
    private readonly RelayCompatControlEngine _relayFallback;
    private readonly bool _allowRelayFallback;
    private readonly ConcurrentQueue<QueuedSignalPublish> _pendingSignalPublishes = new();
    private readonly ConcurrentQueue<QueuedChannelCommand> _pendingChannelCommands = new();
    private int? _lastSignalSequence;
    private int _modeLogCount;
    private bool _dcdRuntimeUnavailable;

    private IRtcPeerConnection? _peerConnection;
    private IRtcDataChannel? _controlChannel;
    private string? _remotePeerId;

    /// <summary>
    /// Creates DataChannelDotNet transport engine.
    /// </summary>
    public DataChannelDotNetControlEngine(ILogger logger, bool allowRelayFallback)
    {
        _logger = logger;
        _relayFallback = new RelayCompatControlEngine(logger);
        _allowRelayFallback = allowRelayFallback;
    }

    /// <inheritdoc />
    public async Task<int?> PollAndProcessCommandsAsync(
        EdgeControlTransportContext context,
        int? lastCommandSequence,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _modeLogCount, 1) == 0)
        {
            _logger.LogInformation(
                "TransportEngine=dcd: data-channel control enabled with direct-signal compatibility and relay fallback={RelayFallback}.",
                _allowRelayFallback);
        }

        var directCount = await PollDcdSignalsAsync(context, cancellationToken);
        directCount += await DrainChannelCommandsAsync(context, cancellationToken);
        directCount += await FlushQueuedSignalPublishesAsync(context, cancellationToken);

        if (directCount > 0)
        {
            return lastCommandSequence;
        }

        if (!_allowRelayFallback)
        {
            return lastCommandSequence;
        }

        return await _relayFallback.PollAndProcessCommandsAsync(context, lastCommandSequence, cancellationToken);
    }

    /// <summary>
    /// Polls signaling inbox and processes DCD signaling/control messages.
    /// </summary>
    private async Task<int> PollDcdSignalsAsync(
        EdgeControlTransportContext context,
        CancellationToken cancellationToken)
    {
        var query = $"/api/v1/sessions/{Uri.EscapeDataString(context.EffectiveSessionId)}/transport/webrtc/signals?recipientPeerId={Uri.EscapeDataString(context.PeerId)}&limit={context.BatchLimit}";
        if (_lastSignalSequence.HasValue)
        {
            query += $"&afterSequence={_lastSignalSequence.Value}";
        }

        var signals = await context.QuerySignalsAsync(query, cancellationToken);
        var processed = 0;
        foreach (var signal in signals.OrderBy(static x => x.Sequence))
        {
            _lastSignalSequence = signal.Sequence;

            switch (signal.Kind)
            {
                case WebRtcSignalKind.Offer:
                    HandleOfferSignal(signal);
                    processed++;
                    break;
                case WebRtcSignalKind.Answer:
                    HandleAnswerSignal(signal);
                    processed++;
                    break;
                case WebRtcSignalKind.IceCandidate:
                    HandleIceCandidateSignal(signal);
                    processed++;
                    break;
                case WebRtcSignalKind.Bye:
                    HandleByeSignal(signal);
                    processed++;
                    break;
                case WebRtcSignalKind.Command:
                    processed += await ProcessDirectSignalCommandAsync(context, signal, cancellationToken);
                    break;
                case WebRtcSignalKind.Heartbeat:
                case WebRtcSignalKind.Ack:
                default:
                    break;
            }
        }

        return processed;
    }

    /// <summary>
    /// Processes one compatibility direct-signal command and publishes ACK via signaling channel.
    /// </summary>
    private async Task<int> ProcessDirectSignalCommandAsync(
        EdgeControlTransportContext context,
        WebRtcSignalMessageBody signal,
        CancellationToken cancellationToken)
    {
        if (!TryParseTransportCommand(signal.Payload, out var command))
        {
            return 0;
        }

        var request = ToCommandRequest(command, context.EffectiveSessionId);
        var ack = await context.ExecuteCommandAsync(request, cancellationToken);
        ack = MarkDcdDirectMetrics(ack);

        var ackSignal = new WebRtcSignalPublishBody(
            Kind: WebRtcSignalKind.Ack,
            SenderPeerId: context.PeerId,
            RecipientPeerId: signal.SenderPeerId,
            Payload: JsonSerializer.Serialize(ack, JsonOptions));

        var statusCode = await context.PublishSignalAsync(ackSignal, cancellationToken);
        if (statusCode is HttpStatusCode.NotFound)
        {
            _logger.LogDebug(
                "DCD direct signal ACK target was not found for command {CommandId}; senderPeer={SenderPeerId}.",
                request.CommandId,
                signal.SenderPeerId);
        }

        return 1;
    }

    /// <summary>
    /// Handles incoming Offer signaling and generates local Answer.
    /// </summary>
    private void HandleOfferSignal(WebRtcSignalMessageBody signal)
    {
        if (_dcdRuntimeUnavailable)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(signal.SenderPeerId))
        {
            return;
        }

        if (!TryParseDescriptionSdp(signal.Payload, out var offerSdp))
        {
            _logger.LogDebug("Skipping DCD offer signal with invalid payload. Sequence={Sequence}", signal.Sequence);
            return;
        }

        try
        {
            var peer = EnsurePeerConnection(signal.SenderPeerId);
            if (peer is null)
            {
                return;
            }

            peer.SetRemoteDescription(new RtcDescription
            {
                Sdp = offerSdp,
                Type = RtcDescriptionType.Offer,
            });
            peer.SetLocalDescription(RtcDescriptionType.Answer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to handle DCD offer signal for sender {SenderPeerId}. Falling back to compatibility path.",
                signal.SenderPeerId);
        }
    }

    /// <summary>
    /// Handles incoming Answer signaling for established outbound offer.
    /// </summary>
    private void HandleAnswerSignal(WebRtcSignalMessageBody signal)
    {
        if (_dcdRuntimeUnavailable)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(signal.SenderPeerId))
        {
            return;
        }

        if (!TryParseDescriptionSdp(signal.Payload, out var answerSdp))
        {
            _logger.LogDebug("Skipping DCD answer signal with invalid payload. Sequence={Sequence}", signal.Sequence);
            return;
        }

        try
        {
            var peer = EnsurePeerConnection(signal.SenderPeerId);
            if (peer is null)
            {
                return;
            }

            peer.SetRemoteDescription(new RtcDescription
            {
                Sdp = answerSdp,
                Type = RtcDescriptionType.Answer,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to handle DCD answer signal from {SenderPeerId}.", signal.SenderPeerId);
        }
    }

    /// <summary>
    /// Handles incoming ICE candidate signal and applies it to current peer connection.
    /// </summary>
    private void HandleIceCandidateSignal(WebRtcSignalMessageBody signal)
    {
        if (_dcdRuntimeUnavailable)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(signal.SenderPeerId))
        {
            return;
        }

        if (!TryParseIceCandidate(signal, out var candidate, out var mid))
        {
            return;
        }

        try
        {
            var peer = EnsurePeerConnection(signal.SenderPeerId);
            if (peer is null)
            {
                return;
            }

            peer.AddRemoteCandidate(new RtcCandidate
            {
                Content = candidate,
                Mid = mid ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to add DCD remote ICE candidate from {SenderPeerId}.", signal.SenderPeerId);
        }
    }

    /// <summary>
    /// Handles explicit BYE signal and clears active DCD peer connection.
    /// </summary>
    private void HandleByeSignal(WebRtcSignalMessageBody signal)
    {
        if (string.IsNullOrWhiteSpace(signal.SenderPeerId))
        {
            return;
        }

        lock (_stateGate)
        {
            if (!string.Equals(_remotePeerId, signal.SenderPeerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ResetPeerConnectionUnsafe();
            _logger.LogInformation("DCD peer connection closed by BYE from {SenderPeerId}.", signal.SenderPeerId);
        }
    }

    /// <summary>
    /// Flushes queued local signaling events produced by peer callbacks.
    /// </summary>
    private async Task<int> FlushQueuedSignalPublishesAsync(
        EdgeControlTransportContext context,
        CancellationToken cancellationToken)
    {
        var published = 0;
        while (_pendingSignalPublishes.TryDequeue(out var item))
        {
            if (string.IsNullOrWhiteSpace(item.RecipientPeerId))
            {
                continue;
            }

            var request = new WebRtcSignalPublishBody(
                Kind: item.Kind,
                SenderPeerId: context.PeerId,
                RecipientPeerId: item.RecipientPeerId,
                Payload: item.Payload,
                Mid: item.Mid,
                MLineIndex: item.MLineIndex);

            var status = await context.PublishSignalAsync(request, cancellationToken);
            if (status == HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "Queued DCD signaling target not found. Kind={Kind}, Recipient={RecipientPeerId}",
                    item.Kind,
                    item.RecipientPeerId);
            }

            published++;
        }

        return published;
    }

    /// <summary>
    /// Executes queued channel commands and responds with ACK over channel (or signaling fallback).
    /// </summary>
    private async Task<int> DrainChannelCommandsAsync(
        EdgeControlTransportContext context,
        CancellationToken cancellationToken)
    {
        var processed = 0;
        while (_pendingChannelCommands.TryDequeue(out var queued))
        {
            var request = ToCommandRequest(queued.Command, context.EffectiveSessionId);
            var ack = await context.ExecuteCommandAsync(request, cancellationToken);
            ack = MarkDcdDirectMetrics(ack);
            var ackPayload = JsonSerializer.Serialize(ack, JsonOptions);

            if (!TrySendAckViaDataChannel(ackPayload))
            {
                await PublishAckSignalFallbackAsync(context, queued.SenderPeerId, ackPayload, request.CommandId, cancellationToken);
            }

            processed++;
        }

        return processed;
    }

    /// <summary>
    /// Attempts to send ACK payload over open control data channel.
    /// </summary>
    private bool TrySendAckViaDataChannel(string payload)
    {
        IRtcDataChannel? channel;
        lock (_stateGate)
        {
            channel = _controlChannel;
        }

        if (channel is null || channel.IsClosed || !channel.IsOpen)
        {
            return false;
        }

        try
        {
            channel.Send(payload);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send DCD ACK over data channel.");
            return false;
        }
    }

    /// <summary>
    /// Publishes ACK via signaling channel when data-channel ACK delivery is unavailable.
    /// </summary>
    private async Task PublishAckSignalFallbackAsync(
        EdgeControlTransportContext context,
        string? recipientPeerId,
        string ackPayload,
        string commandId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipientPeerId))
        {
            _logger.LogDebug(
                "Skipped DCD ACK signaling fallback because sender peer is unknown. CommandId={CommandId}",
                commandId);
            return;
        }

        var status = await context.PublishSignalAsync(
            new WebRtcSignalPublishBody(
                Kind: WebRtcSignalKind.Ack,
                SenderPeerId: context.PeerId,
                RecipientPeerId: recipientPeerId,
                Payload: ackPayload),
            cancellationToken);

        if (status == HttpStatusCode.NotFound)
        {
            _logger.LogDebug(
                "DCD ACK signaling fallback target was not found. CommandId={CommandId}, Recipient={RecipientPeerId}",
                commandId,
                recipientPeerId);
        }
    }

    /// <summary>
    /// Ensures one DCD peer connection exists for current remote sender.
    /// Replaces existing peer when sender changes.
    /// </summary>
    private IRtcPeerConnection? EnsurePeerConnection(string senderPeerId)
    {
        lock (_stateGate)
        {
            if (_peerConnection is not null
                && string.Equals(_remotePeerId, senderPeerId, StringComparison.OrdinalIgnoreCase))
            {
                return _peerConnection;
            }

            ResetPeerConnectionUnsafe();
            _remotePeerId = senderPeerId;

            try
            {
                var peer = new RtcPeerConnection(new RtcPeerConfiguration());
                peer.OnLocalDescriptionSafe += (_, desc) => HandleLocalDescription(desc, senderPeerId);
                peer.OnCandidateSafe += (_, candidate) => HandleLocalCandidate(candidate, senderPeerId);
                peer.OnDataChannel += (_, channel) => HandleDataChannelOpened(channel, senderPeerId);
                peer.OnConnectionStateChange += (_, state) =>
                {
                    _logger.LogDebug("DCD connection state changed. Remote={RemotePeerId}, State={State}", senderPeerId, state);
                };

                _peerConnection = peer;
                return peer;
            }
            catch (Exception ex)
            {
                _dcdRuntimeUnavailable = true;
                _logger.LogWarning(
                    ex,
                    "Failed to initialize DataChannelDotNet runtime; DCD engine will keep compatibility/fallback paths.");
                return null;
            }
        }
    }

    /// <summary>
    /// Handles local description callback and queues signaling publish request.
    /// </summary>
    private void HandleLocalDescription(RtcDescription description, string recipientPeerId)
    {
        var kind = description.Type switch
        {
            RtcDescriptionType.Offer => WebRtcSignalKind.Offer,
            RtcDescriptionType.Answer => WebRtcSignalKind.Answer,
            _ => (WebRtcSignalKind?)null,
        };

        if (kind is null || string.IsNullOrWhiteSpace(description.Sdp))
        {
            return;
        }

        _pendingSignalPublishes.Enqueue(new QueuedSignalPublish(
            Kind: kind.Value,
            RecipientPeerId: recipientPeerId,
            Payload: description.Sdp,
            Mid: null,
            MLineIndex: null));
    }

    /// <summary>
    /// Handles local ICE candidate callback and queues signaling publish request.
    /// </summary>
    private void HandleLocalCandidate(RtcCandidate candidate, string recipientPeerId)
    {
        if (string.IsNullOrWhiteSpace(candidate.Content))
        {
            return;
        }

        _pendingSignalPublishes.Enqueue(new QueuedSignalPublish(
            Kind: WebRtcSignalKind.IceCandidate,
            RecipientPeerId: recipientPeerId,
            Payload: candidate.Content,
            Mid: string.IsNullOrWhiteSpace(candidate.Mid) ? null : candidate.Mid,
            MLineIndex: null));
    }

    /// <summary>
    /// Handles newly opened data channel and subscribes to command payload callbacks.
    /// </summary>
    private void HandleDataChannelOpened(IRtcDataChannel channel, string remotePeerId)
    {
        if (!string.Equals(channel.Label, ControlChannelLabel, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Ignoring DCD data channel '{Label}'. Expected '{ExpectedLabel}'.",
                channel.Label,
                ControlChannelLabel);
            return;
        }

        lock (_stateGate)
        {
            _controlChannel = channel;
        }

        channel.OnTextReceivedSafe += (_, received) => HandleChannelText(remotePeerId, received.Text);
        channel.OnClose += _ =>
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_controlChannel, channel))
                {
                    _controlChannel = null;
                }
            }

            _logger.LogDebug("DCD control data channel closed for remote {RemotePeerId}.", remotePeerId);
        };

        _logger.LogInformation("DCD control data channel is open. Remote={RemotePeerId}, Label={Label}", remotePeerId, channel.Label);
    }

    /// <summary>
    /// Parses one data-channel text payload into queued transport command.
    /// </summary>
    private void HandleChannelText(string senderPeerId, string payload)
    {
        if (!TryParseTransportCommand(payload, out var command))
        {
            return;
        }

        _pendingChannelCommands.Enqueue(new QueuedChannelCommand(senderPeerId, command));
    }

    /// <summary>
    /// Resets active peer-connection objects under state lock.
    /// </summary>
    private void ResetPeerConnectionUnsafe()
    {
        try
        {
            _controlChannel?.Dispose();
        }
        catch
        {
        }

        try
        {
            _peerConnection?.Dispose();
        }
        catch
        {
        }

        _controlChannel = null;
        _peerConnection = null;
        _remotePeerId = null;
    }

    /// <summary>
    /// Parses one SDP payload from signaling message payload.
    /// Supports raw SDP string or JSON payload with <c>sdp</c> field.
    /// </summary>
    private static bool TryParseDescriptionSdp(string payload, out string sdp)
    {
        sdp = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var trimmed = payload.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<SignalDescriptionPayload>(trimmed, JsonOptions);
                if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Sdp))
                {
                    sdp = parsed.Sdp.Trim();
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }

            return false;
        }

        sdp = trimmed;
        return true;
    }

    /// <summary>
    /// Parses one ICE candidate payload from signaling message.
    /// Supports raw candidate string and JSON payload with candidate/mid fields.
    /// </summary>
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

    /// <summary>
    /// Parses one transport command payload.
    /// </summary>
    private bool TryParseTransportCommand(string payload, out TransportCommandMessageBody command)
    {
        command = default!;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<TransportCommandMessageBody>(payload, JsonOptions);
            if (parsed is null || parsed.Kind != TransportMessageKind.Command || string.IsNullOrWhiteSpace(parsed.CommandId))
            {
                return false;
            }

            command = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse DCD command payload.");
            return false;
        }
    }

    /// <summary>
    /// Converts transport command message into command-dispatch request.
    /// </summary>
    private static CommandRequestBody ToCommandRequest(
        TransportCommandMessageBody command,
        string fallbackSessionId)
    {
        return new CommandRequestBody(
            CommandId: command.CommandId,
            SessionId: string.IsNullOrWhiteSpace(command.SessionId) ? fallbackSessionId : command.SessionId,
            Channel: CommandChannel.DataChannel,
            Action: command.Action,
            Args: ToArgsDictionary(command.Args),
            TimeoutMs: Math.Max(1000, command.TimeoutMs),
            IdempotencyKey: $"dcd:{command.CommandId}");
    }

    /// <summary>
    /// Converts typed HID args back to dynamic command args dictionary expected by worker command executor.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ToArgsDictionary(TransportHidCommandArgsBody args)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        AddIfNotNull(result, "text", args.Text);
        AddIfNotNull(result, "shortcut", args.Shortcut);
        AddIfNotNull(result, "usage", args.Usage);
        AddIfNotNull(result, "modifiers", args.Modifiers);
        AddIfNotNull(result, "dx", args.Dx);
        AddIfNotNull(result, "dy", args.Dy);
        AddIfNotNull(result, "wheel", args.Wheel);
        AddIfNotNull(result, "delta", args.Delta);
        AddIfNotNull(result, "button", args.Button);
        AddIfNotNull(result, "down", args.Down);
        AddIfNotNull(result, "holdMs", args.HoldMs);
        AddIfNotNull(result, "itfSel", args.InterfaceSelector);

        return result;
    }

    /// <summary>
    /// Adds value only when it is explicitly provided.
    /// </summary>
    private static void AddIfNotNull<T>(IDictionary<string, object?> target, string key, T value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    /// <summary>
    /// Adds deterministic metric markers for direct DCD mode.
    /// </summary>
    private static TransportAckMessageBody MarkDcdDirectMetrics(TransportAckMessageBody ack)
    {
        var metrics = ack.Metrics is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(ack.Metrics, StringComparer.OrdinalIgnoreCase);

        metrics["transportEngineDcdDirect"] = 1d;

        return ack with { Metrics = metrics };
    }

    private sealed record SignalDescriptionPayload(string? Sdp, string? Type);

    private sealed record SignalIceCandidatePayload(string? Candidate, string? Mid = null, int? MLineIndex = null);

    private sealed record QueuedSignalPublish(
        WebRtcSignalKind Kind,
        string RecipientPeerId,
        string Payload,
        string? Mid,
        int? MLineIndex);

    private sealed record QueuedChannelCommand(string? SenderPeerId, TransportCommandMessageBody Command);
}
