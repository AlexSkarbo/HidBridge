using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Builds collaboration-oriented read models from persisted session, event, and command data.
/// </summary>
public sealed class CollaborationReadModelService
{
    private readonly ISessionStore _sessionStore;
    private readonly IEventStore _eventStore;
    private readonly ICommandJournalStore _commandJournalStore;

    /// <summary>
    /// Creates the read model service.
    /// </summary>
    /// <param name="sessionStore">Provides persisted session snapshots.</param>
    /// <param name="eventStore">Provides persisted audit and telemetry events.</param>
    /// <param name="commandJournalStore">Provides persisted command journal entries.</param>
    public CollaborationReadModelService(
        ISessionStore sessionStore,
        IEventStore eventStore,
        ICommandJournalStore commandJournalStore)
    {
        _sessionStore = sessionStore;
        _eventStore = eventStore;
        _commandJournalStore = commandJournalStore;
    }

    /// <summary>
    /// Builds the main dashboard projection for one collaboration session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The dashboard read model for the session.</returns>
    public async Task<SessionDashboardReadModel> GetSessionDashboardAsync(string sessionId, CancellationToken cancellationToken)
    {
        var snapshot = await RequireSessionAsync(sessionId, cancellationToken);
        var commands = await _commandJournalStore.ListBySessionAsync(sessionId, cancellationToken);
        var timeline = await GetTimelineEntriesAsync(sessionId, 100, cancellationToken);

        return new SessionDashboardReadModel(
            snapshot.SessionId,
            snapshot.AgentId,
            snapshot.EndpointId,
            snapshot.Profile,
            snapshot.Role,
            snapshot.State,
            snapshot.RequestedBy,
            snapshot.Participants?.Count ?? 0,
            snapshot.Participants?.Count(x => x.Role == SessionRole.Controller) ?? 0,
            snapshot.Participants?.Count(x => x.Role == SessionRole.Observer) ?? 0,
            snapshot.Participants?.Count(x => x.Role == SessionRole.Presenter) ?? 0,
            snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Requested) ?? 0,
            snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Pending) ?? 0,
            snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Accepted) ?? 0,
            snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Rejected) ?? 0,
            snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Revoked) ?? 0,
            commands.Count,
            commands.Count(x => x.Status is CommandStatus.Rejected or CommandStatus.Timeout),
            timeline.Count,
            snapshot.ControlLease?.ParticipantId,
            snapshot.ControlLease?.PrincipalId,
            snapshot.ControlLease?.GrantedBy,
            snapshot.ControlLease?.ExpiresAtUtc,
            commands.OrderByDescending(x => x.CompletedAtUtc ?? x.CreatedAtUtc).Select(x => x.CompletedAtUtc ?? x.CreatedAtUtc).FirstOrDefault(),
            timeline.FirstOrDefault()?.OccurredAtUtc,
            snapshot.LastHeartbeatAtUtc,
            snapshot.LeaseExpiresAtUtc);
    }

    /// <summary>
    /// Builds the participant activity projection for one session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The participant activity set.</returns>
    public async Task<IReadOnlyList<ParticipantActivityReadModel>> GetParticipantActivityAsync(string sessionId, CancellationToken cancellationToken)
    {
        var snapshot = await RequireSessionAsync(sessionId, cancellationToken);
        var commands = await _commandJournalStore.ListBySessionAsync(sessionId, cancellationToken);
        var shares = snapshot.Shares ?? Array.Empty<SessionShareSnapshot>();
        var participants = snapshot.Participants ?? Array.Empty<SessionParticipantSnapshot>();

        return participants
            .Select(participant =>
            {
                var participantCommands = commands
                    .Where(x => string.Equals(x.ParticipantId, participant.ParticipantId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.PrincipalId, participant.PrincipalId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var share = shares.FirstOrDefault(x => string.Equals($"share:{x.ShareId}", participant.ParticipantId, StringComparison.OrdinalIgnoreCase));
                return new ParticipantActivityReadModel(
                    participant.ParticipantId,
                    participant.PrincipalId,
                    participant.Role,
                    participant.JoinedAtUtc,
                    participant.UpdatedAtUtc,
                    participantCommands.Length,
                    participantCommands.Count(x => x.Status is CommandStatus.Rejected or CommandStatus.Timeout),
                    participantCommands
                        .OrderByDescending(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
                        .Select(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
                        .FirstOrDefault(),
                    share is not null,
                    share?.ShareId,
                    string.Equals(snapshot.ControlLease?.ParticipantId, participant.ParticipantId, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(x => x.CommandCount)
            .ThenBy(x => x.ParticipantId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Builds the invitation and share dashboard for one session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The share dashboard read model.</returns>
    public async Task<ShareDashboardReadModel> GetShareDashboardAsync(string sessionId, CancellationToken cancellationToken)
    {
        var snapshot = await RequireSessionAsync(sessionId, cancellationToken);
        var shares = snapshot.Shares ?? Array.Empty<SessionShareSnapshot>();
        var requested = shares.Where(x => x.Status == SessionShareStatus.Requested).OrderByDescending(x => x.UpdatedAtUtc).ToArray();
        var pending = shares.Where(x => x.Status == SessionShareStatus.Pending).OrderByDescending(x => x.UpdatedAtUtc).ToArray();
        var accepted = shares.Where(x => x.Status == SessionShareStatus.Accepted).OrderByDescending(x => x.UpdatedAtUtc).ToArray();
        var rejected = shares.Where(x => x.Status == SessionShareStatus.Rejected).OrderByDescending(x => x.UpdatedAtUtc).ToArray();
        var revoked = shares.Where(x => x.Status == SessionShareStatus.Revoked).OrderByDescending(x => x.UpdatedAtUtc).ToArray();

        return new ShareDashboardReadModel(
            snapshot.SessionId,
            requested.Length,
            pending.Length,
            accepted.Length,
            rejected.Length,
            revoked.Length,
            requested,
            pending,
            accepted,
            rejected,
            revoked);
    }

    /// <summary>
    /// Builds the operator-focused timeline view for one session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="principalId">Optional principal filter.</param>
    /// <param name="take">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The filtered operator timeline read model.</returns>
    public async Task<OperatorTimelineReadModel> GetOperatorTimelineAsync(string sessionId, string? principalId, int take, CancellationToken cancellationToken)
    {
        var entries = await GetTimelineEntriesAsync(sessionId, take, cancellationToken);

        var filtered = string.IsNullOrWhiteSpace(principalId)
            ? entries
            : entries
                .Where(x => x.Data is IReadOnlyDictionary<string, object?> data
                    && data.TryGetValue("principalId", out var value)
                    && string.Equals(value?.ToString(), principalId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        return new OperatorTimelineReadModel(sessionId, filtered.Count, filtered);
    }

    /// <summary>
    /// Builds the control-arbitration dashboard for one session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The current control dashboard read model.</returns>
    public async Task<ControlDashboardReadModel> GetControlDashboardAsync(string sessionId, CancellationToken cancellationToken)
    {
        var snapshot = await RequireSessionAsync(sessionId, cancellationToken);
        var participants = snapshot.Participants ?? Array.Empty<SessionParticipantSnapshot>();
        var candidates = participants
            .Select(participant => new ControlCandidateReadModel(
                participant.ParticipantId,
                participant.PrincipalId,
                participant.Role,
                participant.Role is SessionRole.Owner or SessionRole.Controller or SessionRole.Presenter,
                string.Equals(snapshot.ControlLease?.ParticipantId, participant.ParticipantId, StringComparison.OrdinalIgnoreCase),
                participant.JoinedAtUtc,
                participant.UpdatedAtUtc))
            .OrderByDescending(x => x.IsCurrentController)
            .ThenByDescending(x => x.IsEligible)
            .ThenBy(x => x.ParticipantId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ControlDashboardReadModel(snapshot.SessionId, snapshot.ControlLease, candidates);
    }

    private async Task<SessionSnapshot> RequireSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var snapshot = (await _sessionStore.ListAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        return snapshot ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");
    }

    private async Task<IReadOnlyList<TimelineEntryBody>> GetTimelineEntriesAsync(string sessionId, int take, CancellationToken cancellationToken)
    {
        var audit = await _eventStore.ListAuditAsync(cancellationToken);
        var telemetry = await _eventStore.ListTelemetryAsync(cancellationToken);
        var commands = await _commandJournalStore.ListBySessionAsync(sessionId, cancellationToken);
        return TimelineComposer.Compose(audit, telemetry, commands, sessionId, take);
    }
}
