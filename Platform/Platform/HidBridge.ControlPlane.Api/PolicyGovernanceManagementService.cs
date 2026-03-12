using System.Text.Json;
using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Handles policy scope and assignment management flows for the operator control plane.
/// </summary>
public sealed class PolicyGovernanceManagementService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IPolicyStore _policyStore;
    private readonly IPolicyRevisionStore _policyRevisionStore;
    private readonly IEventWriter _eventWriter;

    /// <summary>
    /// Creates the policy governance management service.
    /// </summary>
    public PolicyGovernanceManagementService(
        IPolicyStore policyStore,
        IPolicyRevisionStore policyRevisionStore,
        IEventWriter eventWriter)
    {
        _policyStore = policyStore;
        _policyRevisionStore = policyRevisionStore;
        _eventWriter = eventWriter;
    }

    /// <summary>
    /// Lists visible policy scopes for the current caller.
    /// </summary>
    public async Task<IReadOnlyList<PolicyScopeReadModel>> ListScopesAsync(ApiCallerContext caller, CancellationToken cancellationToken)
    {
        var scopes = await _policyStore.ListScopesAsync(cancellationToken);
        return FilterVisibleScopes(scopes, caller)
            .OrderBy(static scope => scope.ScopeId, StringComparer.OrdinalIgnoreCase)
            .Select(static scope => new PolicyScopeReadModel(
                scope.ScopeId,
                scope.TenantId,
                scope.OrganizationId,
                scope.ViewerRoleRequired,
                scope.ModeratorOverrideEnabled,
                scope.AdminOverrideEnabled,
                scope.UpdatedAtUtc,
                scope.IsActive))
            .ToArray();
    }

    /// <summary>
    /// Lists visible policy assignments for the current caller.
    /// </summary>
    public async Task<IReadOnlyList<PolicyAssignmentReadModel>> ListAssignmentsAsync(ApiCallerContext caller, CancellationToken cancellationToken)
    {
        var scopes = await _policyStore.ListScopesAsync(cancellationToken);
        var visibleScopeIds = FilterVisibleScopes(scopes, caller)
            .Select(static scope => scope.ScopeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignments = await _policyStore.ListAssignmentsAsync(cancellationToken);

        return assignments
            .Where(assignment => visibleScopeIds.Contains(assignment.ScopeId))
            .OrderBy(static assignment => assignment.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static assignment => assignment.PrincipalId, StringComparer.OrdinalIgnoreCase)
            .Select(static assignment => new PolicyAssignmentReadModel(
                assignment.AssignmentId,
                assignment.ScopeId,
                assignment.PrincipalId,
                assignment.Roles,
                assignment.UpdatedAtUtc,
                assignment.Source,
                assignment.IsActive))
            .ToArray();
    }

    /// <summary>
    /// Creates or updates one policy scope and appends the corresponding revision/audit entries.
    /// </summary>
    public async Task<PolicyScopeReadModel> UpsertScopeAsync(ApiCallerContext caller, PolicyScopeUpsertBody body, CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var normalizedScopeId = string.IsNullOrWhiteSpace(body.ScopeId)
            ? throw new ArgumentException("scopeId is required.", nameof(body))
            : body.ScopeId.Trim();
        var snapshot = new PolicyScopeSnapshot(
            normalizedScopeId,
            NullIfWhitespace(body.TenantId),
            NullIfWhitespace(body.OrganizationId),
            body.ViewerRoleRequired,
            body.ModeratorOverrideEnabled,
            body.AdminOverrideEnabled,
            nowUtc,
            body.IsActive);

        await _policyStore.UpsertScopeAsync(snapshot, cancellationToken);

        var revisions = await _policyRevisionStore.ListAsync(cancellationToken);
        await _policyRevisionStore.AppendAsync(
            BuildRevision(
                "scope",
                snapshot.ScopeId,
                snapshot.ScopeId,
                null,
                GetNextVersion(revisions, "scope", snapshot.ScopeId),
                JsonSerializer.Serialize(snapshot, SerializerOptions),
                nowUtc,
                "policy-management"),
            cancellationToken);

        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                Category: "policy",
                Message: $"Policy scope {snapshot.ScopeId} upserted by {caller.PrincipalId ?? caller.SubjectId}",
                SessionId: null,
                Data: new Dictionary<string, object?>
                {
                    ["scopeId"] = snapshot.ScopeId,
                    ["tenantId"] = snapshot.TenantId,
                    ["organizationId"] = snapshot.OrganizationId,
                    ["viewerRoleRequired"] = snapshot.ViewerRoleRequired,
                    ["moderatorOverrideEnabled"] = snapshot.ModeratorOverrideEnabled,
                    ["adminOverrideEnabled"] = snapshot.AdminOverrideEnabled,
                    ["isActive"] = snapshot.IsActive,
                    ["actedBy"] = caller.PrincipalId ?? caller.SubjectId,
                    ["source"] = "policy-management",
                },
                CreatedAtUtc: nowUtc),
            cancellationToken);

        return new PolicyScopeReadModel(
            snapshot.ScopeId,
            snapshot.TenantId,
            snapshot.OrganizationId,
            snapshot.ViewerRoleRequired,
            snapshot.ModeratorOverrideEnabled,
            snapshot.AdminOverrideEnabled,
            snapshot.UpdatedAtUtc,
            snapshot.IsActive);
    }

    /// <summary>
    /// Activates or deactivates one policy scope.
    /// </summary>
    public async Task<PolicyScopeReadModel> SetScopeActivationAsync(
        ApiCallerContext caller,
        string scopeId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var existingScopes = await _policyStore.ListScopesAsync(cancellationToken);
        var existing = existingScopes.FirstOrDefault(x =>
            string.Equals(x.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Policy scope {scopeId} was not found.");

        var nowUtc = DateTimeOffset.UtcNow;
        var snapshot = existing with
        {
            IsActive = isActive,
            UpdatedAtUtc = nowUtc,
        };

        await _policyStore.UpsertScopeAsync(snapshot, cancellationToken);

        var revisions = await _policyRevisionStore.ListAsync(cancellationToken);
        await _policyRevisionStore.AppendAsync(
            BuildRevision(
                "scope",
                snapshot.ScopeId,
                snapshot.ScopeId,
                null,
                GetNextVersion(revisions, "scope", snapshot.ScopeId),
                JsonSerializer.Serialize(snapshot, SerializerOptions),
                nowUtc,
                "policy-management"),
            cancellationToken);

        var action = isActive ? "activated" : "deactivated";
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                Category: "policy",
                Message: $"Policy scope {snapshot.ScopeId} {action} by {caller.PrincipalId ?? caller.SubjectId}",
                SessionId: null,
                Data: new Dictionary<string, object?>
                {
                    ["scopeId"] = snapshot.ScopeId,
                    ["tenantId"] = snapshot.TenantId,
                    ["organizationId"] = snapshot.OrganizationId,
                    ["viewerRoleRequired"] = snapshot.ViewerRoleRequired,
                    ["moderatorOverrideEnabled"] = snapshot.ModeratorOverrideEnabled,
                    ["adminOverrideEnabled"] = snapshot.AdminOverrideEnabled,
                    ["isActive"] = snapshot.IsActive,
                    ["actedBy"] = caller.PrincipalId ?? caller.SubjectId,
                    ["source"] = "policy-management",
                },
                CreatedAtUtc: nowUtc),
            cancellationToken);

        return new PolicyScopeReadModel(
            snapshot.ScopeId,
            snapshot.TenantId,
            snapshot.OrganizationId,
            snapshot.ViewerRoleRequired,
            snapshot.ModeratorOverrideEnabled,
            snapshot.AdminOverrideEnabled,
            snapshot.UpdatedAtUtc,
            snapshot.IsActive);
    }

    /// <summary>
    /// Deletes one policy scope after all assignments have been removed.
    /// </summary>
    public async Task DeleteScopeAsync(
        ApiCallerContext caller,
        string scopeId,
        CancellationToken cancellationToken)
    {
        var existingScopes = await _policyStore.ListScopesAsync(cancellationToken);
        var existing = existingScopes.FirstOrDefault(x =>
            string.Equals(x.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Policy scope {scopeId} was not found.");

        var existingAssignments = await _policyStore.ListAssignmentsAsync(cancellationToken);
        var linkedAssignments = existingAssignments
            .Where(x => string.Equals(x.ScopeId, existing.ScopeId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.AssignmentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (linkedAssignments.Length > 0)
        {
            throw new InvalidOperationException($"Policy scope {existing.ScopeId} still has {linkedAssignments.Length} assignment(s). Remove assignments before deleting the scope.");
        }

        await _policyStore.RemoveScopeAsync(existing.ScopeId, cancellationToken);

        var nowUtc = DateTimeOffset.UtcNow;
        var revisions = await _policyRevisionStore.ListAsync(cancellationToken);
        await _policyRevisionStore.AppendAsync(
            BuildRevision(
                "scope",
                existing.ScopeId,
                existing.ScopeId,
                null,
                GetNextVersion(revisions, "scope", existing.ScopeId),
                JsonSerializer.Serialize(
                    new Dictionary<string, object?>
                    {
                        ["scopeId"] = existing.ScopeId,
                        ["tenantId"] = existing.TenantId,
                        ["organizationId"] = existing.OrganizationId,
                        ["viewerRoleRequired"] = existing.ViewerRoleRequired,
                        ["moderatorOverrideEnabled"] = existing.ModeratorOverrideEnabled,
                        ["adminOverrideEnabled"] = existing.AdminOverrideEnabled,
                        ["isActive"] = existing.IsActive,
                        ["deleted"] = true,
                    },
                    SerializerOptions),
                nowUtc,
                "policy-management"),
            cancellationToken);

        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                Category: "policy",
                Message: $"Policy scope {existing.ScopeId} deleted by {caller.PrincipalId ?? caller.SubjectId}",
                SessionId: null,
                Data: new Dictionary<string, object?>
                {
                    ["scopeId"] = existing.ScopeId,
                    ["tenantId"] = existing.TenantId,
                    ["organizationId"] = existing.OrganizationId,
                    ["viewerRoleRequired"] = existing.ViewerRoleRequired,
                    ["moderatorOverrideEnabled"] = existing.ModeratorOverrideEnabled,
                    ["adminOverrideEnabled"] = existing.AdminOverrideEnabled,
                    ["isActive"] = existing.IsActive,
                    ["actedBy"] = caller.PrincipalId ?? caller.SubjectId,
                    ["deleted"] = true,
                },
                CreatedAtUtc: nowUtc),
            cancellationToken);
    }

    /// <summary>
    /// Creates or updates one policy assignment and appends the corresponding revision/audit entries.
    /// </summary>
    public async Task<PolicyAssignmentReadModel> UpsertAssignmentAsync(ApiCallerContext caller, PolicyAssignmentUpsertBody body, CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var scopeId = string.IsNullOrWhiteSpace(body.ScopeId)
            ? throw new ArgumentException("scopeId is required.", nameof(body))
            : body.ScopeId.Trim();
        var principalId = string.IsNullOrWhiteSpace(body.PrincipalId)
            ? throw new ArgumentException("principalId is required.", nameof(body))
            : body.PrincipalId.Trim();
        var normalizedRoles = OperatorPolicyRoles.Normalize(body.Roles);
        var assignmentId = string.IsNullOrWhiteSpace(body.AssignmentId)
            ? $"{scopeId}:{principalId}".ToLowerInvariant()
            : body.AssignmentId.Trim();

        var snapshot = new PolicyAssignmentSnapshot(
            assignmentId,
            scopeId,
            principalId,
            normalizedRoles,
            nowUtc,
            NullIfWhitespace(body.Source) ?? "policy-management",
            body.IsActive);

        await _policyStore.UpsertAssignmentAsync(snapshot, cancellationToken);

        var revisions = await _policyRevisionStore.ListAsync(cancellationToken);
        await _policyRevisionStore.AppendAsync(
            BuildRevision(
                "assignment",
                snapshot.AssignmentId,
                snapshot.ScopeId,
                snapshot.PrincipalId,
                GetNextVersion(revisions, "assignment", snapshot.AssignmentId),
                JsonSerializer.Serialize(snapshot, SerializerOptions),
                nowUtc,
                snapshot.Source ?? "policy-management"),
            cancellationToken);

        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                Category: "policy",
                Message: $"Policy assignment {snapshot.AssignmentId} upserted by {caller.PrincipalId ?? caller.SubjectId}",
                SessionId: null,
                Data: new Dictionary<string, object?>
                {
                    ["scopeId"] = snapshot.ScopeId,
                    ["assignmentId"] = snapshot.AssignmentId,
                    ["principalId"] = snapshot.PrincipalId,
                    ["roles"] = snapshot.Roles,
                    ["source"] = snapshot.Source,
                    ["isActive"] = snapshot.IsActive,
                    ["actedBy"] = caller.PrincipalId ?? caller.SubjectId,
                },
                CreatedAtUtc: nowUtc),
            cancellationToken);

        return new PolicyAssignmentReadModel(
            snapshot.AssignmentId,
            snapshot.ScopeId,
            snapshot.PrincipalId,
            snapshot.Roles,
            snapshot.UpdatedAtUtc,
            snapshot.Source,
            snapshot.IsActive);
    }

    /// <summary>
    /// Activates or deactivates one policy assignment.
    /// </summary>
    public async Task<PolicyAssignmentReadModel> SetAssignmentActivationAsync(
        ApiCallerContext caller,
        string assignmentId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var existingAssignments = await _policyStore.ListAssignmentsAsync(cancellationToken);
        var existing = existingAssignments.FirstOrDefault(x =>
            string.Equals(x.AssignmentId, assignmentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Policy assignment {assignmentId} was not found.");

        var nowUtc = DateTimeOffset.UtcNow;
        var snapshot = existing with
        {
            IsActive = isActive,
            UpdatedAtUtc = nowUtc,
            Source = existing.Source ?? "policy-management",
        };

        await _policyStore.UpsertAssignmentAsync(snapshot, cancellationToken);

        var revisions = await _policyRevisionStore.ListAsync(cancellationToken);
        await _policyRevisionStore.AppendAsync(
            BuildRevision(
                "assignment",
                snapshot.AssignmentId,
                snapshot.ScopeId,
                snapshot.PrincipalId,
                GetNextVersion(revisions, "assignment", snapshot.AssignmentId),
                JsonSerializer.Serialize(snapshot, SerializerOptions),
                nowUtc,
                snapshot.Source ?? "policy-management"),
            cancellationToken);

        var action = isActive ? "activated" : "deactivated";
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                Category: "policy",
                Message: $"Policy assignment {snapshot.AssignmentId} {action} by {caller.PrincipalId ?? caller.SubjectId}",
                SessionId: null,
                Data: new Dictionary<string, object?>
                {
                    ["scopeId"] = snapshot.ScopeId,
                    ["assignmentId"] = snapshot.AssignmentId,
                    ["principalId"] = snapshot.PrincipalId,
                    ["roles"] = snapshot.Roles,
                    ["source"] = snapshot.Source,
                    ["isActive"] = snapshot.IsActive,
                    ["actedBy"] = caller.PrincipalId ?? caller.SubjectId,
                },
                CreatedAtUtc: nowUtc),
            cancellationToken);

        return new PolicyAssignmentReadModel(
            snapshot.AssignmentId,
            snapshot.ScopeId,
            snapshot.PrincipalId,
            snapshot.Roles,
            snapshot.UpdatedAtUtc,
            snapshot.Source,
            snapshot.IsActive);
    }

    /// <summary>
    /// Deletes one policy assignment and appends tombstone revision/audit entries.
    /// </summary>
    public async Task DeleteAssignmentAsync(
        ApiCallerContext caller,
        string assignmentId,
        CancellationToken cancellationToken)
    {
        var existingAssignments = await _policyStore.ListAssignmentsAsync(cancellationToken);
        var existing = existingAssignments.FirstOrDefault(x =>
            string.Equals(x.AssignmentId, assignmentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Policy assignment {assignmentId} was not found.");

        await _policyStore.RemoveAssignmentAsync(existing.AssignmentId, cancellationToken);

        var nowUtc = DateTimeOffset.UtcNow;
        var revisions = await _policyRevisionStore.ListAsync(cancellationToken);
        await _policyRevisionStore.AppendAsync(
            BuildRevision(
                "assignment",
                existing.AssignmentId,
                existing.ScopeId,
                existing.PrincipalId,
                GetNextVersion(revisions, "assignment", existing.AssignmentId),
                JsonSerializer.Serialize(
                    new Dictionary<string, object?>
                    {
                        ["assignmentId"] = existing.AssignmentId,
                        ["scopeId"] = existing.ScopeId,
                        ["principalId"] = existing.PrincipalId,
                        ["roles"] = existing.Roles,
                        ["source"] = existing.Source,
                        ["deleted"] = true,
                    },
                    SerializerOptions),
                nowUtc,
                existing.Source ?? "policy-management"),
            cancellationToken);

        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                Category: "policy",
                Message: $"Policy assignment {existing.AssignmentId} deleted by {caller.PrincipalId ?? caller.SubjectId}",
                SessionId: null,
                Data: new Dictionary<string, object?>
                {
                    ["scopeId"] = existing.ScopeId,
                    ["assignmentId"] = existing.AssignmentId,
                    ["principalId"] = existing.PrincipalId,
                    ["roles"] = existing.Roles,
                    ["source"] = existing.Source,
                    ["actedBy"] = caller.PrincipalId ?? caller.SubjectId,
                    ["deleted"] = true,
                },
                CreatedAtUtc: nowUtc),
            cancellationToken);
    }

    private static PolicyScopeSnapshot[] FilterVisibleScopes(IReadOnlyList<PolicyScopeSnapshot> scopes, ApiCallerContext caller)
    {
        if (!caller.IsPresent || caller.IsAdmin || !caller.HasScope)
        {
            return scopes.ToArray();
        }

        return scopes
            .Where(scope =>
                (string.IsNullOrWhiteSpace(scope.TenantId)
                    || string.Equals(scope.TenantId, caller.TenantId, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(scope.OrganizationId)
                    || string.Equals(scope.OrganizationId, caller.OrganizationId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static int GetNextVersion(IReadOnlyList<PolicyRevisionSnapshot> revisions, string entityType, string entityId)
        => revisions
            .Where(x =>
                string.Equals(x.EntityType, entityType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.EntityId, entityId, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Version)
            .DefaultIfEmpty(0)
            .Max() + 1;

    private static PolicyRevisionSnapshot BuildRevision(
        string entityType,
        string entityId,
        string scopeId,
        string? principalId,
        int version,
        string snapshotJson,
        DateTimeOffset createdAtUtc,
        string source)
        => new(
            $"{entityType}:{entityId}:v{version}".ToLowerInvariant(),
            entityType,
            entityId,
            scopeId,
            principalId,
            version,
            snapshotJson,
            createdAtUtc,
            source);

    private static string? NullIfWhitespace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
