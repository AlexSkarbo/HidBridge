using HidBridge.Abstractions;

namespace HidBridge.SessionOrchestrator;

/// <summary>
/// Resolves effective caller scope and operator roles from persisted policy assignments.
/// </summary>
internal sealed class OperatorPolicyService : IOperatorPolicyService
{
    private readonly IPolicyStore _policyStore;

    /// <summary>
    /// Initializes the shared operator policy service.
    /// </summary>
    public OperatorPolicyService(IPolicyStore policyStore)
    {
        _policyStore = policyStore;
    }

    /// <inheritdoc />
    public async Task<OperatorPolicyResolution> ResolveAsync(
        string? principalId,
        string? tenantId,
        string? organizationId,
        IReadOnlyList<string>? operatorRoles,
        CancellationToken cancellationToken)
    {
        var normalizedRoles = OperatorPolicyRoles.Normalize(operatorRoles);
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return new OperatorPolicyResolution(
                principalId,
                tenantId,
                organizationId,
                normalizedRoles,
                [],
                []);
        }

        var assignments = await _policyStore.ListAssignmentsAsync(cancellationToken);
        var matchingAssignments = assignments
            .Where(x =>
                x.IsActive
                && string.Equals(x.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matchingAssignments.Length == 0)
        {
            return new OperatorPolicyResolution(
                principalId,
                tenantId,
                organizationId,
                normalizedRoles,
                [],
                []);
        }

        var scopes = await _policyStore.ListScopesAsync(cancellationToken);
        var scopeLookup = scopes.ToDictionary(x => x.ScopeId, StringComparer.OrdinalIgnoreCase);
        var matchedScopes = matchingAssignments
            .Select(x => scopeLookup.GetValueOrDefault(x.ScopeId))
            .Where(static x => x is not null && x.IsActive)
            .Cast<PolicyScopeSnapshot>()
            .ToArray();

        var activeScopeIds = matchedScopes
            .Select(static x => x.ScopeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        matchingAssignments = matchingAssignments
            .Where(x => activeScopeIds.Contains(x.ScopeId))
            .ToArray();

        var effectiveRoles = normalizedRoles
            .Concat(matchingAssignments.SelectMany(x => x.Roles))
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static role => role, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolvedScope = ResolveScope(matchedScopes, tenantId, organizationId);

        return new OperatorPolicyResolution(
            principalId,
            tenantId ?? resolvedScope?.TenantId,
            organizationId ?? resolvedScope?.OrganizationId,
            effectiveRoles,
            matchingAssignments,
            matchedScopes);
    }

    private static PolicyScopeSnapshot? ResolveScope(
        IReadOnlyList<PolicyScopeSnapshot> scopes,
        string? tenantId,
        string? organizationId)
    {
        if (scopes.Count == 0)
        {
            return null;
        }

        var exact = scopes.FirstOrDefault(scope =>
            (string.IsNullOrWhiteSpace(tenantId)
                || string.Equals(scope.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(organizationId)
                || string.Equals(scope.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase)));

        if (exact is not null)
        {
            return exact;
        }

        var distinctScopes = scopes
            .Select(scope => new { scope.ScopeId, scope.TenantId, scope.OrganizationId })
            .Distinct()
            .ToArray();

        if (distinctScopes.Length == 1)
        {
            return scopes[0];
        }

        if (string.IsNullOrWhiteSpace(tenantId) && string.IsNullOrWhiteSpace(organizationId))
        {
            return null;
        }

        var tenantOnly = scopes
            .Where(scope =>
                !string.IsNullOrWhiteSpace(tenantId)
                && string.Equals(scope.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(scope => scope.ScopeId)
            .ToArray();

        return tenantOnly.Length == 1 ? tenantOnly[0] : null;
    }
}
