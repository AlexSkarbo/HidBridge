namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Represents aggregated policy governance diagnostics for runtime operations.
/// </summary>
public sealed record PolicyDiagnosticsSummaryReadModel(
    DateTimeOffset GeneratedAtUtc,
    int TotalRevisions,
    int VisibleScopeCount,
    int VisibleAssignmentCount,
    int PruneCandidateCount,
    DateTimeOffset? LatestRevisionAtUtc,
    int RetentionDays,
    int MaxRevisionsPerEntity,
    IReadOnlyList<PolicyRevisionEntityTypeSummaryReadModel> EntityTypes,
    IReadOnlyList<PolicyRevisionScopeSummaryReadModel> Scopes);

/// <summary>
/// Represents one policy revision entity-type bucket.
/// </summary>
public sealed record PolicyRevisionEntityTypeSummaryReadModel(
    string EntityType,
    int Count,
    DateTimeOffset? LatestRevisionAtUtc);

/// <summary>
/// Represents one visible policy scope bucket.
/// </summary>
public sealed record PolicyRevisionScopeSummaryReadModel(
    string ScopeId,
    string? TenantId,
    string? OrganizationId,
    int RevisionCount,
    int AssignmentCount,
    DateTimeOffset? LatestRevisionAtUtc);

/// <summary>
/// Represents the result of a manual policy revision prune operation.
/// </summary>
public sealed record PolicyRevisionPruneResultReadModel(
    DateTimeOffset ExecutedAtUtc,
    int DeletedRevisionCount,
    int RetentionDays,
    int MaxRevisionsPerEntity);

/// <summary>
/// Represents one editable policy scope row.
/// </summary>
public sealed record PolicyScopeReadModel(
    string ScopeId,
    string? TenantId,
    string? OrganizationId,
    bool ViewerRoleRequired,
    bool ModeratorOverrideEnabled,
    bool AdminOverrideEnabled,
    DateTimeOffset UpdatedAtUtc,
    bool IsActive);

/// <summary>
/// Represents one editable policy assignment row.
/// </summary>
public sealed record PolicyAssignmentReadModel(
    string AssignmentId,
    string ScopeId,
    string PrincipalId,
    IReadOnlyList<string> Roles,
    DateTimeOffset UpdatedAtUtc,
    string? Source,
    bool IsActive);

/// <summary>
/// Represents one scope upsert request body.
/// </summary>
public sealed record PolicyScopeUpsertBody(
    string ScopeId,
    string? TenantId,
    string? OrganizationId,
    bool ViewerRoleRequired,
    bool ModeratorOverrideEnabled,
    bool AdminOverrideEnabled,
    bool IsActive = true);

/// <summary>
/// Represents one assignment upsert request body.
/// </summary>
public sealed record PolicyAssignmentUpsertBody(
    string? AssignmentId,
    string ScopeId,
    string PrincipalId,
    IReadOnlyList<string> Roles,
    string? Source,
    bool IsActive = true);
