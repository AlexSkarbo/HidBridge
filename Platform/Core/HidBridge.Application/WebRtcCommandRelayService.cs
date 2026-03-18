using HidBridge.Contracts;
using HidBridge.Abstractions;
using System.Collections.Concurrent;

namespace HidBridge.Application;

/// <summary>
/// Maintains transient WebRTC peer presence, command relay queue, and command acknowledgments per session.
/// </summary>
public sealed class WebRtcCommandRelayService
{
    private readonly ConcurrentDictionary<string, SessionRelayState> _sessions = new(StringComparer.OrdinalIgnoreCase);

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
        return Task.FromResult(state.UpsertPeer(sessionId, peerId, endpointId, isOnline: true, metadata));
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
        return Task.FromResult(state.UpsertPeer(sessionId, peerId, endpointId: null, isOnline: false, metadata: null));
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

        return Task.FromResult(state.ListPeers());
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

        return _sessions.TryGetValue(sessionId, out var state) && state.HasOnlinePeer(endpointId);
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
        return Task.FromResult(state.EnqueueCommand(sessionId, route.EndpointId, command));
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

        return Task.FromResult(state.PublishAck(ack));
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
            });
        }

        return Task.FromResult(state.GetMetrics(endpointId));
    }

    private sealed class SessionRelayState
    {
        private int _nextCommandSequence;
        private readonly ConcurrentDictionary<string, WebRtcPeerStateBody> _peers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<WebRtcCommandEnvelopeBody> _commands = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandAckBody>> _pendingAcks = new(StringComparer.OrdinalIgnoreCase);
        private DateTimeOffset? _lastRelayAckAtUtc;

        public WebRtcPeerStateBody UpsertPeer(
            string sessionId,
            string peerId,
            string? endpointId,
            bool isOnline,
            IReadOnlyDictionary<string, string>? metadata)
        {
            var nowUtc = DateTimeOffset.UtcNow;
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
            return snapshot;
        }

        public IReadOnlyList<WebRtcPeerStateBody> ListPeers()
            => _peers.Values
                .OrderBy(x => x.PeerId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        public bool HasOnlinePeer(string? endpointId)
            => _peers.Values.Any(peer =>
                peer.IsOnline
                && (string.IsNullOrWhiteSpace(endpointId)
                    || string.IsNullOrWhiteSpace(peer.EndpointId)
                    || string.Equals(peer.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)));

        public WebRtcCommandEnvelopeBody EnqueueCommand(
            string sessionId,
            string? endpointId,
            CommandRequestBody command)
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
                CreatedAtUtc: DateTimeOffset.UtcNow);

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

        public bool PublishAck(CommandAckBody ack)
        {
            _lastRelayAckAtUtc = DateTimeOffset.UtcNow;
            if (!_pendingAcks.TryRemove(ack.CommandId, out var pendingAck))
            {
                return false;
            }

            pendingAck.TrySetResult(ack);
            return true;
        }

        public IReadOnlyDictionary<string, object?> GetMetrics(string? endpointId)
        {
            var matchingPeers = _peers.Values
                .Where(peer =>
                    string.IsNullOrWhiteSpace(endpointId)
                    || string.IsNullOrWhiteSpace(peer.EndpointId)
                    || string.Equals(peer.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(peer => peer.LastSeenAtUtc)
                .ToArray();
            var onlinePeerCount = matchingPeers.Count(peer => peer.IsOnline);
            var latestPeer = matchingPeers.FirstOrDefault();
            var latestMetadata = latestPeer?.Metadata;
            return new Dictionary<string, object?>
            {
                ["onlinePeerCount"] = onlinePeerCount,
                ["pendingAckCount"] = _pendingAcks.Count,
                ["queuedCommandCount"] = _commands.Count,
                ["lastRelayAckAtUtc"] = _lastRelayAckAtUtc,
                ["lastPeerSeenAtUtc"] = latestPeer?.LastSeenAtUtc,
                ["lastPeerState"] = TryReadMetadataValue(latestMetadata, "state"),
                ["lastPeerFailureReason"] = TryReadMetadataValue(latestMetadata, "failureReason"),
                ["lastPeerConsecutiveFailures"] = TryReadMetadataValue(latestMetadata, "consecutiveFailures"),
                ["lastPeerReconnectBackoffMs"] = TryReadMetadataValue(latestMetadata, "reconnectBackoffMs"),
            };
        }

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
    }
}
