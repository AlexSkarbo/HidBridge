namespace HidBridge.Persistence.Sql.Entities;

/// <summary>
/// Stores one persisted tenant/organization policy scope row.
/// </summary>
internal sealed class PolicyScopeEntity
{
    public string ScopeId { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? TenantId { get; set; }
    public string? OrganizationId { get; set; }
    public bool ViewerRoleRequired { get; set; }
    public bool ModeratorOverrideEnabled { get; set; }
    public bool AdminOverrideEnabled { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<PolicyAssignmentEntity> Assignments { get; set; } = new();
}

/// <summary>
/// Stores one persisted principal-to-policy assignment row.
/// </summary>
internal sealed class PolicyAssignmentEntity
{
    public string AssignmentId { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string RolesJson { get; set; } = "[]";
    public string? Source { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public PolicyScopeEntity Scope { get; set; } = null!;
}

/// <summary>
/// Stores one versioned policy revision snapshot row.
/// </summary>
internal sealed class PolicyRevisionEntity
{
    public string RevisionId { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string? PrincipalId { get; set; }
    public int Version { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
}
