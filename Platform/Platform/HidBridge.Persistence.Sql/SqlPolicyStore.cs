using System.Text.Json;
using HidBridge.Abstractions;
using HidBridge.Persistence.Sql.Entities;
using Microsoft.EntityFrameworkCore;

namespace HidBridge.Persistence.Sql;

/// <summary>
/// Persists tenant/organization policy scopes and assignments in PostgreSQL.
/// </summary>
public sealed class SqlPolicyStore : IPolicyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDbContextFactory<PlatformDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes the SQL policy store.
    /// </summary>
    public SqlPolicyStore(IDbContextFactory<PlatformDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Creates or updates one policy scope snapshot.
    /// </summary>
    public async Task UpsertScopeAsync(PolicyScopeSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.PolicyScopes
            .Include(x => x.Assignments)
            .FirstOrDefaultAsync(x => x.ScopeId == snapshot.ScopeId, cancellationToken);

        if (existing is null)
        {
            dbContext.PolicyScopes.Add(new PolicyScopeEntity
            {
                ScopeId = snapshot.ScopeId,
                IsActive = snapshot.IsActive,
                TenantId = snapshot.TenantId,
                OrganizationId = snapshot.OrganizationId,
                ViewerRoleRequired = snapshot.ViewerRoleRequired,
                ModeratorOverrideEnabled = snapshot.ModeratorOverrideEnabled,
                AdminOverrideEnabled = snapshot.AdminOverrideEnabled,
                UpdatedAtUtc = snapshot.UpdatedAtUtc,
            });
        }
        else
        {
            existing.IsActive = snapshot.IsActive;
            existing.TenantId = snapshot.TenantId;
            existing.OrganizationId = snapshot.OrganizationId;
            existing.ViewerRoleRequired = snapshot.ViewerRoleRequired;
            existing.ModeratorOverrideEnabled = snapshot.ModeratorOverrideEnabled;
            existing.AdminOverrideEnabled = snapshot.AdminOverrideEnabled;
            existing.UpdatedAtUtc = snapshot.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Creates or updates one policy assignment snapshot.
    /// </summary>
    public async Task UpsertAssignmentAsync(PolicyAssignmentSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.PolicyAssignments
            .FirstOrDefaultAsync(x => x.AssignmentId == snapshot.AssignmentId, cancellationToken);

        if (existing is null)
        {
            dbContext.PolicyAssignments.Add(ToEntity(snapshot));
        }
        else
        {
            existing.ScopeId = snapshot.ScopeId;
            existing.PrincipalId = snapshot.PrincipalId;
            existing.IsActive = snapshot.IsActive;
            existing.RolesJson = SerializeRoles(snapshot.Roles);
            existing.Source = snapshot.Source;
            existing.UpdatedAtUtc = snapshot.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Removes one persisted policy scope.
    /// </summary>
    public async Task RemoveScopeAsync(string scopeId, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.PolicyScopes
            .FirstOrDefaultAsync(x => x.ScopeId == scopeId, cancellationToken);
        if (existing is null)
        {
            return;
        }

        dbContext.PolicyScopes.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Removes one persisted policy assignment.
    /// </summary>
    public async Task RemoveAssignmentAsync(string assignmentId, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.PolicyAssignments
            .FirstOrDefaultAsync(x => x.AssignmentId == assignmentId, cancellationToken);
        if (existing is null)
        {
            return;
        }

        dbContext.PolicyAssignments.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Lists all policy scope snapshots.
    /// </summary>
    public async Task<IReadOnlyList<PolicyScopeSnapshot>> ListScopesAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await dbContext.PolicyScopes
            .OrderBy(x => x.ScopeId)
            .ToArrayAsync(cancellationToken);

        return entities
            .Select(ToSnapshot)
            .ToArray();
    }

    /// <summary>
    /// Lists all policy assignment snapshots.
    /// </summary>
    public async Task<IReadOnlyList<PolicyAssignmentSnapshot>> ListAssignmentsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await dbContext.PolicyAssignments
            .OrderBy(x => x.ScopeId)
            .ThenBy(x => x.PrincipalId)
            .ToArrayAsync(cancellationToken);

        return entities
            .Select(ToSnapshot)
            .ToArray();
    }

    private static PolicyScopeEntity ToEntity(PolicyScopeSnapshot snapshot)
        => new()
        {
            ScopeId = snapshot.ScopeId,
            IsActive = snapshot.IsActive,
            TenantId = snapshot.TenantId,
            OrganizationId = snapshot.OrganizationId,
            ViewerRoleRequired = snapshot.ViewerRoleRequired,
            ModeratorOverrideEnabled = snapshot.ModeratorOverrideEnabled,
            AdminOverrideEnabled = snapshot.AdminOverrideEnabled,
            UpdatedAtUtc = snapshot.UpdatedAtUtc,
        };

    private static PolicyScopeSnapshot ToSnapshot(PolicyScopeEntity entity)
        => new(
            entity.ScopeId,
            entity.TenantId,
            entity.OrganizationId,
            entity.ViewerRoleRequired,
            entity.ModeratorOverrideEnabled,
            entity.AdminOverrideEnabled,
            entity.UpdatedAtUtc,
            entity.IsActive);

    private static PolicyAssignmentEntity ToEntity(PolicyAssignmentSnapshot snapshot)
        => new()
        {
            AssignmentId = snapshot.AssignmentId,
            ScopeId = snapshot.ScopeId,
            PrincipalId = snapshot.PrincipalId,
            IsActive = snapshot.IsActive,
            RolesJson = SerializeRoles(snapshot.Roles),
            Source = snapshot.Source,
            UpdatedAtUtc = snapshot.UpdatedAtUtc,
        };

    private static PolicyAssignmentSnapshot ToSnapshot(PolicyAssignmentEntity entity)
        => new(
            entity.AssignmentId,
            entity.ScopeId,
            entity.PrincipalId,
            DeserializeRoles(entity.RolesJson),
            entity.UpdatedAtUtc,
            entity.Source,
            entity.IsActive);

    private static string SerializeRoles(IReadOnlyList<string> roles)
        => JsonSerializer.Serialize(
            roles.Where(static role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static role => role, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SerializerOptions);

    private static IReadOnlyList<string> DeserializeRoles(string json)
        => JsonSerializer.Deserialize<string[]>(json, SerializerOptions) ?? [];
}
