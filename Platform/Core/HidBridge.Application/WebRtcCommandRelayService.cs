using HidBridge.Contracts;
using HidBridge.Abstractions;
using System.Collections.Concurrent;

namespace HidBridge.Application;

/// <summary>
/// Maintains transient WebRTC peer presence, command relay queue, and command acknowledgments per session.
/// </summary>
public sealed class WebRtcCommandRelayService
{
    private readonly WebRtcCommandRelayOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, SessionRelayState> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates relay service with default transient-peer options.
    /// </summary>
    public WebRtcCommandRelayService()
        : this(options: null, clock: null)
    {
    }

    /// <summary>
    /// Creates relay service with explicit options and optional clock override (used by tests).
    /// </summary>
    public WebRtcCommandRelayService(
        WebRtcCommandRelayOptions? options,
        Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? new WebRtcCommandRelayOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Marks a peer online for a session and returns the resulting peer snapshot.
    /// </summary>
    public Task<WebRtcPeerStateBody> MarkPeerOnlineAsync(
        string sessionId,
        string peerId,
        string? endpointId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _sessions.GetOrAdd(sessionId, static _ => new SessionRelayState());
        return Task.FromResult(state.UpsertPeer(sessionId, peerId, endpointId, isOnline: true, metadata, _clock()));
    }

    /// <summary>
    /// Marks a peer offline for a session and returns the resulting peer snapshot.
    /// </summary>
    public Task<WebRtcPeerStateBody> MarkPeerOfflineAsync(
        string sessionId,
        string peerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _sessions.GetOrAdd(sessionId, static _ => new SessionRelayState());
        return Task.FromResult(state.UpsertPeer(sessionId, peerId, endpointId: null, isOnline: false, metadata: null, _clock()));
    }

    /// <summary>
    /// Lists currently known peer snapshots for a session.
    /// </summary>
    public Task<IReadOnlyList<WebRtcPeerStateBody>> ListPeersAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return Task.FromResult<IReadOnlyList<WebRtcPeerStateBody>>([]);
        }

        return Task.FromResult(state.ListPeers(_clock(), _options.PeerStaleAfter));
    }

    /// <summary>
    /// Returns true when at least one online peer is available for the supplied session route.
    /// </summary>
    public bool HasOnlinePeer(string? sessionId, string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        return _sessions.TryGetValue(sessionId, out var state) && state.HasOnlinePeer(endpointId, _clock(), _options.PeerStaleAfter);
    }

    /// <summary>
    /// Publishes one command envelope into the relay queue.
    /// </summary>
    public Task<WebRtcCommandEnvelopeBody> EnqueueCommandAsync(
        RealtimeTransportRouteContext route,
        CommandRequestBody command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionId = route.SessionId ?? command.SessionId;
        var state = _sessions.GetOrAdd(sessionId, static _ => new SessionRelayState());
        return Task.FromResult(state.EnqueueCommand(sessionId, route.EndpointId, command, _clock()));
    }

    /// <summary>
    /// Lists command envelopes for a peer using sequence checkpoint filtering.
    /// </summary>
    public Task<IReadOnlyList<WebRtcCommandEnvelopeBody>> ListCommandsAsync(
        string sessionId,
        string peerId,
        int? afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return Task.FromResult<IReadOnlyList<WebRtcCommandEnvelopeBody>>([]);
        }

        return Task.FromResult(state.ListCommands(peerId, afterSequence, limit));
    }

    /// <summary>
    /// Waits for a command acknowledgment previously enqueued for the supplied command id.
    /// </summary>
    public Task<CommandAckBody?> WaitForAckAsync(
        string sessionId,
        string commandId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return Task.FromResult<CommandAckBody?>(null);
        }

        return state.WaitForAckAsync(commandId, timeout, cancellationToken);
    }

    /// <summary>
    /// Publishes one command acknowledgment into the relay pipeline.
    /// </summary>
    public Task<bool> PublishAckAsync(
        string sessionId,
        CommandAckBody ack,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(state.PublishAck(ack, _clock()));
    }

    /// <summary>
    /// Returns relay metrics suitable for transport health diagnostics.
    /// </summary>
    public Task<IReadOnlyDictionary<string, object?>> GetSessionMetricsAsync(
        string? sessionId,
        string? endpointId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var state))
        {
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["onlinePeerCount"] = 0,
                ["pendingAckCount"] = 0,
                ["queuedCommandCount"] = 0,
                ["lastRelayAckAtUtc"] = null,
                ["lastPeerSeenAtUtc"] = null,
                ["lastPeerState"] = null,
                ["lastPeerFailureReason"] = null,
                ["lastPeerConsecutiveFailures"] = null,
                ["lastPeerReconnectBackoffMs"] = null,
                ["firstPeerSeenAtUtc"] = null,
                ["firstPeerReadyAtUtc"] = null,
                ["relayReadyLatencyMs"] = null,
                ["reconnectTransitionCount"] = 0,
                ["connectedTransitionCount"] = 0,
                ["lastPeerMediaReady"] = null,
                ["lastPeerMediaState"] = null,
                ["lastPeerMediaFailureReason"] = null,
                ["lastPeerMediaReportedAtUtc"] = null,
                ["lastPeerMediaStreamId"] = null,
                ["lastPeerMediaSource"] = null,
                ["lastPeerMediaStreamKind"] = null,
                ["lastPeerMediaVideoCodec"] = null,
                ["lastPeerMediaVideoWidth"] = null,
                ["lastPeerMediaVideoHeight"] = null,
                ["lastPeerMediaVideoFrameRate"] = null,
                ["lastPeerMediaVideoBitrateKbps"] = null,
                ["lastPeerMediaAudioCodec"] = null,
                ["lastPeerMediaAudioChannels"] = null,
                ["lastPeerMediaAudioSampleRateHz"] = null,
                ["lastPeerMediaAudioBitrateKbps"] = null,
                ["lastPeerIsStale"] = false,
                ["peerStaleAfterSec"] = _options.PeerStaleAfterSec,
            });
        }

        return Task.FromResult(state.GetMetrics(endpointId, _clock(), _options.PeerStaleAfter));
    }

    private sealed class SessionRelayState
    {
        private int _nextCommandSequence;
        private readonly ConcurrentDictionary<string, WebRtcPeerStateBody> _peers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<WebRtcCommandEnvelopeBody> _commands = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandAckBody>> _pendingAcks = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new();
        private DateTimeOffset? _lastRelayAckAtUtc;
        private DateTimeOffset? _firstPeerSeenAtUtc;
        private DateTimeOffset? _firstPeerReadyAtUtc;
        private int _reconnectTransitionCount;
        private int _connectedTransitionCount;

        public WebRtcPeerStateBody UpsertPeer(
            string sessionId,
            string peerId,
            string? endpointId,
            bool isOnline,
            IReadOnlyDictionary<string, string>? metadata,
            DateTimeOffset nowUtc)
        {
            _peers.TryGetValue(peerId, out var previousSnapshot);
            var snapshot = _peers.AddOrUpdate(
                peerId,
                _ => new WebRtcPeerStateBody(
                    SessionId: sessionId,
                    PeerId: peerId,
                    EndpointId: endpointId,
                    IsOnline: isOnline,
                    LastSeenAtUtc: nowUtc,
                    Metadata: metadata),
                (_, existing) => existing with
                {
                    EndpointId = endpointId ?? existing.EndpointId,
                    IsOnline = isOnline,
                    LastSeenAtUtc = nowUtc,
                    Metadata = metadata ?? existing.Metadata,
                });
            UpdateLifecycleCounters(previousSnapshot?.Metadata, snapshot.Metadata, isOnline, nowUtc);
            return snapshot;
        }

        /// <summary>
        /// Tracks relay lifecycle transitions to support reconnect and ready-latency diagnostics.
        /// </summary>
        private void UpdateLifecycleCounters(
            IReadOnlyDictionary<string, string>? previousMetadata,
            IReadOnlyDictionary<string, string>? currentMetadata,
            bool isOnline,
            DateTimeOffset nowUtc)
        {
            lock (_sync)
            {
                if (isOnline && !_firstPeerSeenAtUtc.HasValue)
                {
                    _firstPeerSeenAtUtc = ResolveStateChangedAtUtc(currentMetadata) ?? nowUtc;
                }

                var previousState = TryReadMetadataValue(previousMetadata, "state");
                var currentState = TryReadMetadataValue(currentMetadata, "state");
                if (string.IsNullOrWhiteSpace(currentState)
                    || string.Equals(previousState, currentState, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.Equals(currentState, "Reconnecting", StringComparison.OrdinalIgnoreCase))
                {
                    _reconnectTransitionCount++;
                }

                if (string.Equals(currentState, "Connected", StringComparison.OrdinalIgnoreCase))
                {
                    _connectedTransitionCount++;
                    if (!_firstPeerReadyAtUtc.HasValue && IsPeerReady(currentMetadata))
                    {
                        _firstPeerReadyAtUtc = ResolveStateChangedAtUtc(currentMetadata) ?? nowUtc;
                    }
                }
            }
        }

        public IReadOnlyList<WebRtcPeerStateBody> ListPeers(DateTimeOffset nowUtc, TimeSpan peerStaleAfter)
            => _peers.Values
                .OrderBy(x => x.PeerId, StringComparer.OrdinalIgnoreCase)
                .Select(peer => IsPeerStale(peer, nowUtc, peerStaleAfter)
                    ? peer with { IsOnline = false }
                    : peer)
                .ToArray();

        public bool HasOnlinePeer(string? endpointId, DateTimeOffset nowUtc, TimeSpan peerStaleAfter)
            => _peers.Values.Any(peer =>
                IsPeerAvailable(peer, nowUtc, peerStaleAfter)
                && (string.IsNullOrWhiteSpace(endpointId)
                    || string.IsNullOrWhiteSpace(peer.EndpointId)
                    || string.Equals(peer.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)));

        public WebRtcCommandEnvelopeBody EnqueueCommand(
            string sessionId,
            string? endpointId,
            CommandRequestBody command,
            DateTimeOffset nowUtc)
        {
            var sequence = Interlocked.Increment(ref _nextCommandSequence);
            var recipientPeerId = command.Args.TryGetValue("recipientPeerId", out var recipientValue)
                ? recipientValue?.ToString()
                : null;
            var envelope = new WebRtcCommandEnvelopeBody(
                SessionId: sessionId,
                Sequence: sequence,
                EndpointId: endpointId,
                RecipientPeerId: string.IsNullOrWhiteSpace(recipientPeerId) ? null : recipientPeerId,
                Command: command,
                CreatedAtUtc: nowUtc);

            _commands.Enqueue(envelope);
            while (_commands.Count > 2000 && _commands.TryDequeue(out _))
            {
            }

            _pendingAcks[command.CommandId] = new TaskCompletionSource<CommandAckBody>(TaskCreationOptions.RunContinuationsAsynchronously);
            return envelope;
        }

        public IReadOnlyList<WebRtcCommandEnvelopeBody> ListCommands(
            string peerId,
            int? afterSequence,
            int limit)
        {
            var effectiveLimit = Math.Clamp(limit, 1, 500);
            return _commands
                .ToArray()
                .Where(x => !afterSequence.HasValue || x.Sequence > afterSequence.Value)
                .Where(x =>
                    string.IsNullOrWhiteSpace(x.RecipientPeerId)
                    || string.Equals(x.RecipientPeerId, peerId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Sequence)
                .Take(effectiveLimit)
                .ToArray();
        }

        public async Task<CommandAckBody?> WaitForAckAsync(
            string commandId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (!_pendingAcks.TryGetValue(commandId, out var pendingAck))
            {
                return null;
            }

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                var ack = await pendingAck.Task.WaitAsync(linkedCts.Token);
                _pendingAcks.TryRemove(commandId, out _);
                return ack;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _pendingAcks.TryRemove(commandId, out _);
                return null;
            }
        }

        public bool PublishAck(CommandAckBody ack, DateTimeOffset nowUtc)
        {
            lock (_sync)
            {
                _lastRelayAckAtUtc = nowUtc;
            }
            if (!_pendingAcks.TryRemove(ack.CommandId, out var pendingAck))
            {
                return false;
            }

            pendingAck.TrySetResult(ack);
            return true;
        }

        public IReadOnlyDictionary<string, object?> GetMetrics(string? endpointId, DateTimeOffset nowUtc, TimeSpan peerStaleAfter)
        {
            var matchingPeers = _peers.Values
                .Where(peer =>
                    string.IsNullOrWhiteSpace(endpointId)
                    || string.IsNullOrWhiteSpace(peer.EndpointId)
                    || string.Equals(peer.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(peer => peer.LastSeenAtUtc)
                .ToArray();
            var onlinePeerCount = matchingPeers.Count(peer => IsPeerAvailable(peer, nowUtc, peerStaleAfter));
            var latestPeer = matchingPeers.FirstOrDefault();
            var latestMetadata = latestPeer?.Metadata;
            var latestPeerIsStale = IsPeerStale(latestPeer, nowUtc, peerStaleAfter);
            DateTimeOffset? lastRelayAckAtUtc;
            DateTimeOffset? firstPeerSeenAtUtc;
            DateTimeOffset? firstPeerReadyAtUtc;
            int reconnectTransitionCount;
            int connectedTransitionCount;
            lock (_sync)
            {
                lastRelayAckAtUtc = _lastRelayAckAtUtc;
                firstPeerSeenAtUtc = _firstPeerSeenAtUtc;
                firstPeerReadyAtUtc = _firstPeerReadyAtUtc;
                reconnectTransitionCount = _reconnectTransitionCount;
                connectedTransitionCount = _connectedTransitionCount;
            }

            var relayReadyLatencyMs = firstPeerSeenAtUtc.HasValue && firstPeerReadyAtUtc.HasValue
                ? Math.Max(0d, (firstPeerReadyAtUtc.Value - firstPeerSeenAtUtc.Value).TotalMilliseconds)
                : (double?)null;
            return new Dictionary<string, object?>
            {
                ["onlinePeerCount"] = onlinePeerCount,
                ["pendingAckCount"] = _pendingAcks.Count,
                ["queuedCommandCount"] = _commands.Count,
                ["lastRelayAckAtUtc"] = lastRelayAckAtUtc,
                ["lastPeerSeenAtUtc"] = latestPeer?.LastSeenAtUtc,
                ["lastPeerState"] = latestPeerIsStale ? "Stale" : TryReadMetadataValue(latestMetadata, "state"),
                ["lastPeerFailureReason"] = latestPeerIsStale ? "peer_stale_timeout" : TryReadMetadataValue(latestMetadata, "failureReason"),
                ["lastPeerConsecutiveFailures"] = TryReadMetadataValue(latestMetadata, "consecutiveFailures"),
                ["lastPeerReconnectBackoffMs"] = TryReadMetadataValue(latestMetadata, "reconnectBackoffMs"),
                ["firstPeerSeenAtUtc"] = firstPeerSeenAtUtc,
                ["firstPeerReadyAtUtc"] = firstPeerReadyAtUtc,
                ["relayReadyLatencyMs"] = relayReadyLatencyMs,
                ["reconnectTransitionCount"] = reconnectTransitionCount,
                ["connectedTransitionCount"] = connectedTransitionCount,
                ["lastPeerMediaReady"] = TryReadMetadataValue(latestMetadata, "mediaReady"),
                ["lastPeerMediaState"] = TryReadMetadataValue(latestMetadata, "mediaState"),
                ["lastPeerMediaFailureReason"] = TryReadMetadataValue(latestMetadata, "mediaFailureReason"),
                ["lastPeerMediaReportedAtUtc"] = TryReadMetadataValue(latestMetadata, "mediaReportedAtUtc"),
                ["lastPeerMediaStreamId"] = TryReadMetadataValue(latestMetadata, "mediaStreamId"),
                ["lastPeerMediaSource"] = TryReadMetadataValue(latestMetadata, "mediaSource"),
                ["lastPeerMediaStreamKind"] = TryReadMetadataValue(latestMetadata, "mediaStreamKind"),
                ["lastPeerMediaVideoCodec"] = TryReadMetadataValue(latestMetadata, "mediaVideoCodec"),
                ["lastPeerMediaVideoWidth"] = TryReadMetadataValue(latestMetadata, "mediaVideoWidth"),
                ["lastPeerMediaVideoHeight"] = TryReadMetadataValue(latestMetadata, "mediaVideoHeight"),
                ["lastPeerMediaVideoFrameRate"] = TryReadMetadataValue(latestMetadata, "mediaVideoFrameRate"),
                ["lastPeerMediaVideoBitrateKbps"] = TryReadMetadataValue(latestMetadata, "mediaVideoBitrateKbps"),
                ["lastPeerMediaAudioCodec"] = TryReadMetadataValue(latestMetadata, "mediaAudioCodec"),
                ["lastPeerMediaAudioChannels"] = TryReadMetadataValue(latestMetadata, "mediaAudioChannels"),
                ["lastPeerMediaAudioSampleRateHz"] = TryReadMetadataValue(latestMetadata, "mediaAudioSampleRateHz"),
                ["lastPeerMediaAudioBitrateKbps"] = TryReadMetadataValue(latestMetadata, "mediaAudioBitrateKbps"),
                ["lastPeerIsStale"] = latestPeerIsStale,
                ["peerStaleAfterSec"] = Math.Round(peerStaleAfter.TotalSeconds),
            };
        }

        /// <summary>
        /// Returns true when one peer can receive relay commands for the current checkpoint.
        /// </summary>
        private static bool IsPeerAvailable(WebRtcPeerStateBody? peer, DateTimeOffset nowUtc, TimeSpan peerStaleAfter)
            => peer is not null && peer.IsOnline && !IsPeerStale(peer, nowUtc, peerStaleAfter);

        /// <summary>
        /// Returns true when peer heartbeat age is outside allowed freshness window.
        /// </summary>
        private static bool IsPeerStale(WebRtcPeerStateBody? peer, DateTimeOffset nowUtc, TimeSpan peerStaleAfter)
            => peer is not null
               && peer.IsOnline
               && nowUtc - peer.LastSeenAtUtc > peerStaleAfter;

        private static string? TryReadMetadataValue(IReadOnlyDictionary<string, string>? metadata, string key)
        {
            if (metadata is null)
            {
                return null;
            }

            return metadata.TryGetValue(key, out var value)
                ? value
                : null;
        }

        private static DateTimeOffset? ResolveStateChangedAtUtc(IReadOnlyDictionary<string, string>? metadata)
        {
            var raw = TryReadMetadataValue(metadata, "stateChangedAtUtc");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return DateTimeOffset.TryParse(raw, out var parsed)
                ? parsed
                : null;
        }

        private static bool IsPeerReady(IReadOnlyDictionary<string, string>? metadata)
        {
            var mediaReadyRaw = TryReadMetadataValue(metadata, "mediaReady");
            if (string.IsNullOrWhiteSpace(mediaReadyRaw))
            {
                return true;
            }

            return bool.TryParse(mediaReadyRaw, out var parsed)
                ? parsed
                : string.Equals(mediaReadyRaw, "1", StringComparison.OrdinalIgnoreCase);
        }
    }
}
