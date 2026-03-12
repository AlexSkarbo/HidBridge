using HidBridge.Persistence.Sql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HidBridge.Persistence.Sql.Configurations;

/// <summary>
/// Configures the persisted policy scope table.
/// </summary>
internal sealed class PolicyScopeEntityConfiguration : IEntityTypeConfiguration<PolicyScopeEntity>
{
    /// <summary>
    /// Builds the EF Core mapping for policy scopes.
    /// </summary>
    public void Configure(EntityTypeBuilder<PolicyScopeEntity> builder)
    {
        builder.ToTable("policy_scopes");
        builder.HasKey(x => x.ScopeId);
        builder.Property(x => x.ScopeId).HasMaxLength(128);
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.Property(x => x.TenantId).HasMaxLength(128);
        builder.Property(x => x.OrganizationId).HasMaxLength(128);
        builder.HasIndex(x => x.IsActive);
        builder.HasIndex(x => x.TenantId);
        builder.HasIndex(x => x.OrganizationId);
        builder.HasMany(x => x.Assignments)
            .WithOne(x => x.Scope)
            .HasForeignKey(x => x.ScopeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Configures the persisted policy assignment table.
/// </summary>
internal sealed class PolicyAssignmentEntityConfiguration : IEntityTypeConfiguration<PolicyAssignmentEntity>
{
    /// <summary>
    /// Builds the EF Core mapping for policy assignments.
    /// </summary>
    public void Configure(EntityTypeBuilder<PolicyAssignmentEntity> builder)
    {
        builder.ToTable("policy_assignments");
        builder.HasKey(x => x.AssignmentId);
        builder.Property(x => x.AssignmentId).HasMaxLength(128);
        builder.Property(x => x.ScopeId).HasMaxLength(128);
        builder.Property(x => x.PrincipalId).HasMaxLength(256);
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.Property(x => x.Source).HasMaxLength(128);
        builder.HasIndex(x => x.ScopeId);
        builder.HasIndex(x => x.PrincipalId);
        builder.HasIndex(x => x.IsActive);
    }
}

/// <summary>
/// Configures the persisted policy revision table.
/// </summary>
internal sealed class PolicyRevisionEntityConfiguration : IEntityTypeConfiguration<PolicyRevisionEntity>
{
    /// <summary>
    /// Builds the EF Core mapping for policy revisions.
    /// </summary>
    public void Configure(EntityTypeBuilder<PolicyRevisionEntity> builder)
    {
        builder.ToTable("policy_revisions");
        builder.HasKey(x => x.RevisionId);
        builder.Property(x => x.RevisionId).HasMaxLength(128);
        builder.Property(x => x.EntityType).HasMaxLength(32);
        builder.Property(x => x.EntityId).HasMaxLength(128);
        builder.Property(x => x.ScopeId).HasMaxLength(128);
        builder.Property(x => x.PrincipalId).HasMaxLength(256);
        builder.Property(x => x.Source).HasMaxLength(128);
        builder.HasIndex(x => x.ScopeId);
        builder.HasIndex(x => x.EntityType);
        builder.HasIndex(x => x.CreatedAtUtc);
    }
}
