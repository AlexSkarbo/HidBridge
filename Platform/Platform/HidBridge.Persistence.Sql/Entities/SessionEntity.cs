namespace HidBridge.Persistence.Sql.Entities;

/// <summary>
/// Stores one persisted control session row.
/// </summary>
internal sealed class SessionEntity
{
    public string SessionId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string EndpointId { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string? OrganizationId { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? ActiveControllerParticipantId { get; set; }
    public string? ActiveControllerPrincipalId { get; set; }
    public string? ControlGrantedBy { get; set; }
    public DateTimeOffset? ControlGrantedAtUtc { get; set; }
    public DateTimeOffset? ControlLeaseExpiresAtUtc { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public List<SessionParticipantEntity> Participants { get; set; } = new();
    public List<SessionShareEntity> Shares { get; set; } = new();
}

/// <summary>
/// Stores one persisted participant bound to a session.
/// </summary>
internal sealed class SessionParticipantEntity
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string ParticipantId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset JoinedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public SessionEntity Session { get; set; } = null!;
}

/// <summary>
/// Stores one persisted share grant bound to a session.
/// </summary>
internal sealed class SessionShareEntity
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string ShareId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public string GrantedBy { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public SessionEntity Session { get; set; } = null!;
}
