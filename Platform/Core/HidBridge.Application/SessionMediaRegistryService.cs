using System.Collections.Concurrent;
using HidBridge.Contracts;

namespace HidBridge.Application;

/// <summary>
/// Stores session-scoped media stream snapshots published by edge runtimes.
/// </summary>
public sealed class SessionMediaRegistryService
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SessionMediaStreamSnapshotBody>> _sessions
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Upserts one media stream snapshot for a session/peer pair.
    /// </summary>
    public Task<SessionMediaStreamSnapshotBody> UpsertAsync(
        string sessionId,
        SessionMediaStreamRegistrationBody registration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nowUtc = DateTimeOffset.UtcNow;
        var normalizedState = string.IsNullOrWhiteSpace(registration.State)
            ? "Unknown"
            : registration.State.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(registration.Source)
            ? null
            : registration.Source.Trim();
        var normalizedPlaybackUrl = string.IsNullOrWhiteSpace(registration.PlaybackUrl)
            ? null
            : registration.PlaybackUrl.Trim();
        var normalizedStreamKind = string.IsNullOrWhiteSpace(registration.StreamKind)
            ? null
            : registration.StreamKind.Trim();
        var snapshot = new SessionMediaStreamSnapshotBody(
            SessionId: sessionId,
            PeerId: registration.PeerId.Trim(),
            EndpointId: registration.EndpointId.Trim(),
            StreamId: registration.StreamId.Trim(),
            Ready: registration.Ready,
            State: normalizedState,
            ReportedAtUtc: registration.ReportedAtUtc,
            UpdatedAtUtc: nowUtc,
            FailureReason: registration.FailureReason,
            Source: normalizedSource,
            PlaybackUrl: normalizedPlaybackUrl,
            StreamKind: normalizedStreamKind,
            Video: registration.Video,
            Audio: registration.Audio,
            Metrics: registration.Metrics);

        var state = _sessions.GetOrAdd(sessionId, static _ => new ConcurrentDictionary<string, SessionMediaStreamSnapshotBody>(StringComparer.OrdinalIgnoreCase));
        var key = BuildStreamKey(snapshot.PeerId, snapshot.StreamId);
        state[key] = snapshot;
        return Task.FromResult(snapshot);
    }

    /// <summary>
    /// Lists media stream snapshots for a session with optional peer/endpoint filters.
    /// </summary>
    public Task<IReadOnlyList<SessionMediaStreamSnapshotBody>> ListAsync(
        string sessionId,
        string? peerId,
        string? endpointId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return Task.FromResult<IReadOnlyList<SessionMediaStreamSnapshotBody>>([]);
        }

        var items = state.Values
            .Where(snapshot => string.IsNullOrWhiteSpace(peerId)
                || string.Equals(snapshot.PeerId, peerId, StringComparison.OrdinalIgnoreCase))
            .Where(snapshot => string.IsNullOrWhiteSpace(endpointId)
                || string.Equals(snapshot.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(snapshot => snapshot.ReportedAtUtc)
            .ThenBy(snapshot => snapshot.StreamId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult<IReadOnlyList<SessionMediaStreamSnapshotBody>>(items);
    }

    /// <summary>
    /// Returns the latest media snapshot for a session route.
    /// </summary>
    public async Task<SessionMediaStreamSnapshotBody?> GetLatestAsync(
        string sessionId,
        string? endpointId,
        CancellationToken cancellationToken)
    {
        var items = await ListAsync(sessionId, peerId: null, endpointId, cancellationToken);
        return items.FirstOrDefault();
    }

    /// <summary>
    /// Builds one stable dictionary key for session media stream snapshots.
    /// </summary>
    private static string BuildStreamKey(string peerId, string streamId)
        => $"{peerId.Trim()}::{streamId.Trim()}";
}
