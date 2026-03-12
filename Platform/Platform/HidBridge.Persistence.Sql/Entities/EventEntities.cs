namespace HidBridge.Persistence.Sql.Entities;

/// <summary>
/// Stores one persisted audit event row.
/// </summary>
internal sealed class AuditEventEntity
{
    public long Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? DataJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// Stores one persisted telemetry event row.
/// </summary>
internal sealed class TelemetryEventEntity
{
    public long Id { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string MetricsJson { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// Stores one persisted command journal row.
/// </summary>
internal sealed class CommandJournalEntity
{
    public long Id { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string ArgsJson { get; set; } = string.Empty;
    public int TimeoutMs { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ParticipantId { get; set; }
    public string? PrincipalId { get; set; }
    public string? ShareId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorJson { get; set; }
    public string? MetricsJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
