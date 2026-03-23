using HidBridge.Abstractions;
using HidBridge.Contracts;
using System.Collections.Concurrent;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Stores transient WebRTC signaling messages per session for browser/agent exchange.
/// </summary>
public sealed class WebRtcSignalingService : IWebRtcSignalingStore
{
    private readonly ConcurrentDictionary<string, SessionSignalState> _signals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Appends one signaling message and returns the persisted snapshot with assigned sequence.
    /// </summary>
    public Task<WebRtcSignalMessageBody> AppendAsync(
        string sessionId,
        WebRtcSignalKind kind,
        string senderPeerId,
        string? recipientPeerId,
        string payload,
        string? mid,
        int? mLineIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = _signals.GetOrAdd(sessionId, static _ => new SessionSignalState());
        var createdAtUtc = DateTimeOffset.UtcNow;
        var sequence = state.NextSequence();
        var message = new WebRtcSignalMessageBody(
            SessionId: sessionId,
            Sequence: sequence,
            Kind: kind,
            SenderPeerId: senderPeerId,
            RecipientPeerId: recipientPeerId,
            Payload: payload,
            Mid: mid,
            MLineIndex: mLineIndex,
            CreatedAtUtc: createdAtUtc);
        state.Append(message);
        return Task.FromResult(message);
    }

    /// <summary>
    /// Lists signaling messages for one session filtered by recipient and sequence checkpoint.
    /// </summary>
    public Task<IReadOnlyList<WebRtcSignalMessageBody>> ListAsync(
        string sessionId,
        string? recipientPeerId,
        int? afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_signals.TryGetValue(sessionId, out var state))
        {
            return Task.FromResult<IReadOnlyList<WebRtcSignalMessageBody>>([]);
        }

        var effectiveLimit = Math.Clamp(limit, 1, 500);
        var filtered = state.List()
            .Where(x => !afterSequence.HasValue || x.Sequence > afterSequence.Value)
            .Where(x =>
                string.IsNullOrWhiteSpace(recipientPeerId)
                || string.IsNullOrWhiteSpace(x.RecipientPeerId)
                || string.Equals(x.RecipientPeerId, recipientPeerId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Sequence)
            .Take(effectiveLimit)
            .ToArray();
        return Task.FromResult<IReadOnlyList<WebRtcSignalMessageBody>>(filtered);
    }

    private sealed class SessionSignalState
    {
        private int _nextSequence;
        private readonly ConcurrentQueue<WebRtcSignalMessageBody> _messages = new();

        public int NextSequence() => Interlocked.Increment(ref _nextSequence);

        public void Append(WebRtcSignalMessageBody message)
        {
            _messages.Enqueue(message);
            // Keep memory bounded for long-lived sessions.
            while (_messages.Count > 2000 && _messages.TryDequeue(out _))
            {
            }
        }

        public IReadOnlyList<WebRtcSignalMessageBody> List() => _messages.ToArray();
    }
}
