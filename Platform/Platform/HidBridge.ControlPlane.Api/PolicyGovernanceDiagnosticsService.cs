using HidBridge.Abstractions;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Builds policy governance diagnostics and operational summaries.
/// </summary>
public sealed class PolicyGovernanceDiagnosticsService
{
    private readonly IPolicyStore _policyStore;
    private readonly IPolicyRevisionStore _revisionStore;
    private readonly PolicyRevisionLifecycleOptions _lifecycleOptions;

    /// <summary>
    /// Creates the policy governance diagnostics service.
    /// </summary>
    public PolicyGovernanceDiagnosticsService(
        IPolicyStore policyStore,
        IPolicyRevisionStore revisionStore,
        PolicyRevisionLifecycleOptions lifecycleOptions)
    {
        _policyStore = policyStore;
        _revisionStore = revisionStore;
        _lifecycleOptions = lifecycleOptions;
    }

    /// <summary>
    /// Builds one aggregate summary over visible policy scopes and revisions.
    /// </summary>
    public async Task<PolicyDiagnosticsSummaryReadModel> GetSummaryAsync(ApiCallerContext caller, CancellationToken cancellationToken)
    {
        var scopes = await _policyStore.ListScopesAsync(cancellationToken);
        var assignments = await _policyStore.ListAssignmentsAsync(cancellationToken);
        var revisions = await _revisionStore.ListAsync(cancellationToken);

        var visibleScopes = FilterVisibleScopes(scopes, caller);
        var visibleScopeIds = visibleScopes
            .Select(static scope => scope.ScopeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var visibleAssignments = assignments
            .Where(assignment => visibleScopeIds.Contains(assignment.ScopeId))
            .ToArray();

        var visibleRevisions = revisions
            .Where(revision => visibleScopeIds.Contains(revision.ScopeId))
            .ToArray();

        return new PolicyDiagnosticsSummaryReadModel(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            TotalRevisions: visibleRevisions.Length,
            VisibleScopeCount: visibleScopes.Length,
            VisibleAssignmentCount: visibleAssignments.Length,
            PruneCandidateCount: CountPruneCandidates(visibleRevisions),
            LatestRevisionAtUtc: visibleRevisions.Length == 0 ? null : visibleRevisions.Max(static revision => revision.CreatedAtUtc),
            RetentionDays: (int)Math.Round(_lifecycleOptions.Retention.TotalDays),
            MaxRevisionsPerEntity: _lifecycleOptions.MaxRevisionsPerEntity,
            EntityTypes: visibleRevisions
                .GroupBy(static revision => revision.EntityType, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => new PolicyRevisionEntityTypeSummaryReadModel(
                    group.Key,
                    group.Count(),
                    group.Max(static revision => revision.CreatedAtUtc)))
                .ToArray(),
            Scopes: visibleScopes
                .OrderBy(static scope => scope.ScopeId, StringComparer.OrdinalIgnoreCase)
                .Select(scope => new PolicyRevisionScopeSummaryReadModel(
                    scope.ScopeId,
                    scope.TenantId,
                    scope.OrganizationId,
                    visibleRevisions.Count(revision => string.Equals(revision.ScopeId, scope.ScopeId, StringComparison.OrdinalIgnoreCase)),
                    visibleAssignments.Count(assignment => string.Equals(assignment.ScopeId, scope.ScopeId, StringComparison.OrdinalIgnoreCase)),
                    visibleRevisions
                        .Where(revision => string.Equals(revision.ScopeId, scope.ScopeId, StringComparison.OrdinalIgnoreCase))
                        .Select(static revision => (DateTimeOffset?)revision.CreatedAtUtc)
                        .OrderByDescending(static value => value)
                        .FirstOrDefault()))
                .ToArray());
    }

    private PolicyScopeSnapshot[] FilterVisibleScopes(IReadOnlyList<PolicyScopeSnapshot> scopes, ApiCallerContext caller)
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

    private int CountPruneCandidates(IReadOnlyList<PolicyRevisionSnapshot> revisions)
    {
        var retainSinceUtc = DateTimeOffset.UtcNow - _lifecycleOptions.Retention;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in revisions.GroupBy(static revision => (revision.EntityType, revision.EntityId)))
        {
            foreach (var item in group
                .OrderByDescending(static revision => revision.CreatedAtUtc)
                .ThenByDescending(static revision => revision.Version)
                .Take(Math.Max(_lifecycleOptions.MaxRevisionsPerEntity, 1)))
            {
                keep.Add(item.RevisionId);
            }

            foreach (var item in group.Where(revision => revision.CreatedAtUtc >= retainSinceUtc))
            {
                keep.Add(item.RevisionId);
            }
        }

        return revisions.Count(revision => !keep.Contains(revision.RevisionId));
    }
}
