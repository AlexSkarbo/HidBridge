using HidBridge.Abstractions;
using HidBridge.Contracts;
using System.Text.Json;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Seeds baseline policy scope and assignment configuration into the configured policy store.
/// </summary>
public sealed class PolicyBootstrapHostedService : IHostedService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IPolicyStore _policyStore;
    private readonly IPolicyRevisionStore _policyRevisionStore;
    private readonly IEventWriter _eventWriter;
    private readonly PolicyBootstrapOptions _options;

    /// <summary>
    /// Initializes the startup bootstrap service.
    /// </summary>
    public PolicyBootstrapHostedService(
        IPolicyStore policyStore,
        IPolicyRevisionStore policyRevisionStore,
        IEventWriter eventWriter,
        PolicyBootstrapOptions options)
    {
        _policyStore = policyStore;
        _policyRevisionStore = policyRevisionStore;
        _eventWriter = eventWriter;
        _options = options;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ScopeId))
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var existingScopes = await _policyStore.ListScopesAsync(cancellationToken);
        var existingAssignments = await _policyStore.ListAssignmentsAsync(cancellationToken);
        var existingRevisions = await _policyRevisionStore.ListAsync(cancellationToken);

        var scopeSnapshot = new PolicyScopeSnapshot(
            _options.ScopeId,
            _options.TenantId,
            _options.OrganizationId,
            _options.ViewerRoleRequired,
            _options.ModeratorOverrideEnabled,
            _options.AdminOverrideEnabled,
            nowUtc);

        var existingScope = existingScopes.FirstOrDefault(x => string.Equals(x.ScopeId, scopeSnapshot.ScopeId, StringComparison.OrdinalIgnoreCase));
        if (!Equals(existingScope, scopeSnapshot))
        {
            await _policyStore.UpsertScopeAsync(scopeSnapshot, cancellationToken);
            await _policyRevisionStore.AppendAsync(
                BuildRevision(
                    "scope",
                    scopeSnapshot.ScopeId,
                    scopeSnapshot.ScopeId,
                    null,
                    GetNextVersion(existingRevisions, "scope", scopeSnapshot.ScopeId),
                    JsonSerializer.Serialize(scopeSnapshot, SerializerOptions),
                    nowUtc,
                    "policy-bootstrap"),
                cancellationToken);

            await _eventWriter.WriteAuditAsync(
                new AuditEventBody(
                    Category: "policy",
                    Message: $"Policy scope {scopeSnapshot.ScopeId} upserted",
                    SessionId: null,
                    Data: new Dictionary<string, object?>
                    {
                        ["scopeId"] = scopeSnapshot.ScopeId,
                        ["tenantId"] = scopeSnapshot.TenantId,
                        ["organizationId"] = scopeSnapshot.OrganizationId,
                        ["viewerRoleRequired"] = scopeSnapshot.ViewerRoleRequired,
                        ["moderatorOverrideEnabled"] = scopeSnapshot.ModeratorOverrideEnabled,
                        ["adminOverrideEnabled"] = scopeSnapshot.AdminOverrideEnabled,
                        ["source"] = "policy-bootstrap",
                    },
                    CreatedAtUtc: nowUtc),
                cancellationToken);
        }

        foreach (var assignment in _options.Assignments
                     .Where(static assignment => !string.IsNullOrWhiteSpace(assignment.PrincipalId)))
        {
            var assignmentId = $"{_options.ScopeId}:{assignment.PrincipalId}".ToLowerInvariant();
            var assignmentSnapshot = new PolicyAssignmentSnapshot(
                assignmentId,
                _options.ScopeId,
                assignment.PrincipalId,
                OperatorPolicyRoles.Normalize(assignment.Roles),
                nowUtc,
                assignment.Source ?? "bootstrap");

            var existingAssignment = existingAssignments.FirstOrDefault(x => string.Equals(x.AssignmentId, assignmentId, StringComparison.OrdinalIgnoreCase));
            if (Equals(existingAssignment, assignmentSnapshot))
            {
                continue;
            }

            await _policyStore.UpsertAssignmentAsync(assignmentSnapshot, cancellationToken);
            await _policyRevisionStore.AppendAsync(
                BuildRevision(
                    "assignment",
                    assignmentSnapshot.AssignmentId,
                    assignmentSnapshot.ScopeId,
                    assignmentSnapshot.PrincipalId,
                    GetNextVersion(existingRevisions, "assignment", assignmentSnapshot.AssignmentId),
                    JsonSerializer.Serialize(assignmentSnapshot, SerializerOptions),
                    nowUtc,
                    assignmentSnapshot.Source ?? "policy-bootstrap"),
                cancellationToken);

            await _eventWriter.WriteAuditAsync(
                new AuditEventBody(
                    Category: "policy",
                    Message: $"Policy assignment {assignmentSnapshot.AssignmentId} upserted",
                    SessionId: null,
                    Data: new Dictionary<string, object?>
                    {
                        ["scopeId"] = assignmentSnapshot.ScopeId,
                        ["assignmentId"] = assignmentSnapshot.AssignmentId,
                        ["principalId"] = assignmentSnapshot.PrincipalId,
                        ["roles"] = assignmentSnapshot.Roles,
                        ["source"] = assignmentSnapshot.Source,
                    },
                    CreatedAtUtc: nowUtc),
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static int GetNextVersion(
        IReadOnlyList<PolicyRevisionSnapshot> revisions,
        string entityType,
        string entityId)
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
}
