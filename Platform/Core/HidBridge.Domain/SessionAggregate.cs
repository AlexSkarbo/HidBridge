using HidBridge.Contracts;

namespace HidBridge.Domain;

/// <summary>
/// Represents the orchestrator-owned state of one active or historical session.
/// </summary>
public sealed class SessionAggregate
{
    /// <summary>
    /// Creates a new session aggregate bound to a target agent and endpoint.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="agentId">The target agent identifier.</param>
    /// <param name="endpointId">The target endpoint identifier.</param>
    /// <param name="profile">The requested session profile.</param>
    /// <param name="requestedBy">The session initiator.</param>
    /// <param name="role">The granted role inside the session.</param>
    public SessionAggregate(
        string sessionId,
        string agentId,
        string endpointId,
        SessionProfile profile,
        string requestedBy,
        SessionRole role,
        IReadOnlyList<SessionParticipantBody>? participants = null,
        IReadOnlyList<SessionShareBody>? shares = null,
        SessionControlLeaseBody? controlLease = null,
        DateTimeOffset? lastHeartbeatAtUtc = null,
        DateTimeOffset? leaseExpiresAtUtc = null,
        string? tenantId = null,
        string? organizationId = null)
    {
        SessionId = sessionId;
        AgentId = agentId;
        EndpointId = endpointId;
        Profile = profile;
        RequestedBy = requestedBy;
        Role = role;
        TenantId = tenantId;
        OrganizationId = organizationId;
        Participants = participants?.ToList() ?? new List<SessionParticipantBody>();
        Shares = shares?.ToList() ?? new List<SessionShareBody>();
        ControlLease = controlLease;
        LastHeartbeatAtUtc = lastHeartbeatAtUtc;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        State = SessionState.Requested;
    }

    /// <summary>
    /// Gets the session identifier.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the bound target agent identifier.
    /// </summary>
    public string AgentId { get; }

    /// <summary>
    /// Gets the bound target endpoint identifier.
    /// </summary>
    public string EndpointId { get; }

    /// <summary>
    /// Gets the requested session quality profile.
    /// </summary>
    public SessionProfile Profile { get; }

    /// <summary>
    /// Gets the user or principal that opened the session.
    /// </summary>
    public string RequestedBy { get; }

    /// <summary>
    /// Gets the owner role granted at session creation time.
    /// </summary>
    public SessionRole Role { get; }

    /// <summary>
    /// Gets the tenant that owns the session scope, when one is assigned.
    /// </summary>
    public string? TenantId { get; }

    /// <summary>
    /// Gets the organization that owns the session scope, when one is assigned.
    /// </summary>
    public string? OrganizationId { get; }

    /// <summary>
    /// Gets the currently known session state.
    /// </summary>
    public SessionState State { get; private set; }

    /// <summary>
    /// Gets the current active participants of the session.
    /// </summary>
    public List<SessionParticipantBody> Participants { get; }

    /// <summary>
    /// Gets the outstanding or historical share grants of the session.
    /// </summary>
    public List<SessionShareBody> Shares { get; }

    /// <summary>
    /// Gets the currently active control lease, when one exists.
    /// </summary>
    public SessionControlLeaseBody? ControlLease { get; private set; }

    /// <summary>
    /// Gets the timestamp of the latest orchestration heartbeat.
    /// </summary>
    public DateTimeOffset? LastHeartbeatAtUtc { get; private set; }

    /// <summary>
    /// Gets the timestamp when the current lease expires.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    /// <summary>
    /// Transitions the session aggregate to its next state.
    /// </summary>
    /// <param name="next">The next lifecycle state.</param>
    public void MoveTo(SessionState next) => State = next;

    /// <summary>
    /// Updates the active lease timestamps for the session.
    /// </summary>
    /// <param name="lastHeartbeatAtUtc">The latest heartbeat time.</param>
    /// <param name="leaseExpiresAtUtc">The computed lease expiry time.</param>
    public void SetLease(DateTimeOffset lastHeartbeatAtUtc, DateTimeOffset leaseExpiresAtUtc)
    {
        LastHeartbeatAtUtc = lastHeartbeatAtUtc;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
    }

    /// <summary>
    /// Replaces the participant set with the supplied snapshot list.
    /// </summary>
    /// <param name="participants">The participant snapshot list.</param>
    public void ReplaceParticipants(IReadOnlyList<SessionParticipantBody> participants)
    {
        Participants.Clear();
        Participants.AddRange(participants);
    }

    /// <summary>
    /// Replaces the share grant set with the supplied snapshot list.
    /// </summary>
    /// <param name="shares">The share snapshot list.</param>
    public void ReplaceShares(IReadOnlyList<SessionShareBody> shares)
    {
        Shares.Clear();
        Shares.AddRange(shares);
    }

    /// <summary>
    /// Replaces the active control lease snapshot.
    /// </summary>
    /// <param name="controlLease">The new control lease snapshot or <see langword="null"/>.</param>
    public void ReplaceControlLease(SessionControlLeaseBody? controlLease)
    {
        ControlLease = controlLease;
    }

    /// <summary>
    /// Creates or updates one participant inside the session.
    /// </summary>
    /// <param name="participant">The participant snapshot to apply.</param>
    /// <returns>The stored participant snapshot.</returns>
    public SessionParticipantBody UpsertParticipant(SessionParticipantBody participant)
    {
        var index = Participants.FindIndex(x => string.Equals(x.ParticipantId, participant.ParticipantId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            Participants[index] = participant;
        }
        else
        {
            Participants.Add(participant);
        }

        return participant;
    }

    /// <summary>
    /// Removes one participant from the session.
    /// </summary>
    /// <param name="participantId">The participant identifier to remove.</param>
    /// <returns><see langword="true"/> when the participant existed and was removed.</returns>
    public bool RemoveParticipant(string participantId)
    {
        var removed = Participants.RemoveAll(x => string.Equals(x.ParticipantId, participantId, StringComparison.OrdinalIgnoreCase));
        return removed > 0;
    }

    /// <summary>
    /// Creates or updates a share grant in the session.
    /// </summary>
    /// <param name="share">The share grant snapshot to apply.</param>
    /// <returns>The stored share snapshot.</returns>
    public SessionShareBody UpsertShare(SessionShareBody share)
    {
        var index = Shares.FindIndex(x => string.Equals(x.ShareId, share.ShareId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            Shares[index] = share;
        }
        else
        {
            Shares.Add(share);
        }

        return share;
    }

    /// <summary>
    /// Attempts to read one share grant by identifier.
    /// </summary>
    /// <param name="shareId">The share identifier.</param>
    /// <returns>The share snapshot when found; otherwise <see langword="null"/>.</returns>
    public SessionShareBody? FindShare(string shareId)
        => Shares.FirstOrDefault(x => string.Equals(x.ShareId, shareId, StringComparison.OrdinalIgnoreCase));
}
