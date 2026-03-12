using HidBridge.Abstractions;

namespace HidBridge.Persistence;

/// <summary>
/// Persists policy scopes and assignments in JSON files.
/// </summary>
public sealed class FilePolicyStore : IPolicyStore
{
    private readonly JsonFileStore<List<PolicyScopeSnapshot>> _scopeStore;
    private readonly JsonFileStore<List<PolicyAssignmentSnapshot>> _assignmentStore;

    /// <summary>
    /// Initializes the file-backed policy store.
    /// </summary>
    public FilePolicyStore(FilePersistenceOptions options)
    {
        _scopeStore = new JsonFileStore<List<PolicyScopeSnapshot>>(options.PolicyScopesPath, () => new List<PolicyScopeSnapshot>());
        _assignmentStore = new JsonFileStore<List<PolicyAssignmentSnapshot>>(options.PolicyAssignmentsPath, () => new List<PolicyAssignmentSnapshot>());
    }

    /// <summary>
    /// Creates or updates one policy scope snapshot.
    /// </summary>
    public Task UpsertScopeAsync(PolicyScopeSnapshot snapshot, CancellationToken cancellationToken)
        => _scopeStore.UpdateAsync(items => items
            .Where(x => !string.Equals(x.ScopeId, snapshot.ScopeId, StringComparison.OrdinalIgnoreCase))
            .Append(snapshot)
            .OrderBy(x => x.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ToList(), cancellationToken);

    /// <summary>
    /// Creates or updates one policy assignment snapshot.
    /// </summary>
    public Task UpsertAssignmentAsync(PolicyAssignmentSnapshot snapshot, CancellationToken cancellationToken)
        => _assignmentStore.UpdateAsync(items => items
            .Where(x => !string.Equals(x.AssignmentId, snapshot.AssignmentId, StringComparison.OrdinalIgnoreCase))
            .Append(snapshot with
            {
                Roles = snapshot.Roles
                    .Where(static role => !string.IsNullOrWhiteSpace(role))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static role => role, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            })
            .OrderBy(x => x.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PrincipalId, StringComparer.OrdinalIgnoreCase)
            .ToList(), cancellationToken);

    /// <summary>
    /// Removes one persisted policy scope snapshot.
    /// </summary>
    public Task RemoveScopeAsync(string scopeId, CancellationToken cancellationToken)
        => _scopeStore.UpdateAsync(items => items
            .Where(x => !string.Equals(x.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ToList(), cancellationToken);

    /// <summary>
    /// Removes one persisted policy assignment snapshot.
    /// </summary>
    public Task RemoveAssignmentAsync(string assignmentId, CancellationToken cancellationToken)
        => _assignmentStore.UpdateAsync(items => items
            .Where(x => !string.Equals(x.AssignmentId, assignmentId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PrincipalId, StringComparer.OrdinalIgnoreCase)
            .ToList(), cancellationToken);

    /// <summary>
    /// Lists all policy scope snapshots.
    /// </summary>
    public async Task<IReadOnlyList<PolicyScopeSnapshot>> ListScopesAsync(CancellationToken cancellationToken)
        => (await _scopeStore.ReadAsync(cancellationToken))
            .OrderBy(x => x.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Lists all policy assignment snapshots.
    /// </summary>
    public async Task<IReadOnlyList<PolicyAssignmentSnapshot>> ListAssignmentsAsync(CancellationToken cancellationToken)
        => (await _assignmentStore.ReadAsync(cancellationToken))
            .OrderBy(x => x.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.PrincipalId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
