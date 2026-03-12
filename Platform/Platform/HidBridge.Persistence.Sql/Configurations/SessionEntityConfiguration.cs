using HidBridge.Persistence.Sql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HidBridge.Persistence.Sql.Configurations;

/// <summary>
/// Configures the persisted session snapshot table.
/// </summary>
internal sealed class SessionEntityConfiguration : IEntityTypeConfiguration<SessionEntity>
{
    /// <summary>
    /// Builds the EF Core mapping for session snapshots.
    /// </summary>
    /// <param name="builder">The entity builder.</param>
    public void Configure(EntityTypeBuilder<SessionEntity> builder)
    {
        builder.ToTable("sessions");
        builder.HasKey(x => x.SessionId);
        builder.Property(x => x.SessionId).HasMaxLength(128);
        builder.Property(x => x.AgentId).HasMaxLength(128);
        builder.Property(x => x.EndpointId).HasMaxLength(128);
        builder.Property(x => x.Profile).HasMaxLength(64);
        builder.Property(x => x.RequestedBy).HasMaxLength(256);
        builder.Property(x => x.TenantId).HasMaxLength(128);
        builder.Property(x => x.OrganizationId).HasMaxLength(128);
        builder.Property(x => x.Role).HasMaxLength(64);
        builder.Property(x => x.State).HasMaxLength(64);
        builder.Property(x => x.ActiveControllerParticipantId).HasMaxLength(128);
        builder.Property(x => x.ActiveControllerPrincipalId).HasMaxLength(256);
        builder.Property(x => x.ControlGrantedBy).HasMaxLength(256);
        builder.HasIndex(x => x.AgentId);
        builder.HasIndex(x => x.EndpointId);
        builder.HasIndex(x => x.TenantId);
        builder.HasIndex(x => x.OrganizationId);
        builder.HasIndex(x => x.State);
        builder.HasIndex(x => x.LeaseExpiresAtUtc);
        builder.HasIndex(x => x.ActiveControllerPrincipalId);
        builder.HasIndex(x => x.ControlLeaseExpiresAtUtc);
        builder.HasMany(x => x.Participants)
            .WithOne(x => x.Session)
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Shares)
            .WithOne(x => x.Session)
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Configures the persisted session participant table.
/// </summary>
internal sealed class SessionParticipantEntityConfiguration : IEntityTypeConfiguration<SessionParticipantEntity>
{
    /// <summary>
    /// Builds the EF Core mapping for session participants.
    /// </summary>
    /// <param name="builder">The entity builder.</param>
    public void Configure(EntityTypeBuilder<SessionParticipantEntity> builder)
    {
        builder.ToTable("session_participants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SessionId).HasMaxLength(128);
        builder.Property(x => x.ParticipantId).HasMaxLength(128);
        builder.Property(x => x.PrincipalId).HasMaxLength(256);
        builder.Property(x => x.Role).HasMaxLength(64);
        builder.HasIndex(x => new { x.SessionId, x.ParticipantId }).IsUnique();
        builder.HasIndex(x => x.PrincipalId);
    }
}

/// <summary>
/// Configures the persisted session share table.
/// </summary>
internal sealed class SessionShareEntityConfiguration : IEntityTypeConfiguration<SessionShareEntity>
{
    /// <summary>
    /// Builds the EF Core mapping for session shares.
    /// </summary>
    /// <param name="builder">The entity builder.</param>
    public void Configure(EntityTypeBuilder<SessionShareEntity> builder)
    {
        builder.ToTable("session_shares");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SessionId).HasMaxLength(128);
        builder.Property(x => x.ShareId).HasMaxLength(128);
        builder.Property(x => x.PrincipalId).HasMaxLength(256);
        builder.Property(x => x.GrantedBy).HasMaxLength(256);
        builder.Property(x => x.Role).HasMaxLength(64);
        builder.Property(x => x.Status).HasMaxLength(64);
        builder.HasIndex(x => new { x.SessionId, x.ShareId }).IsUnique();
        builder.HasIndex(x => x.PrincipalId);
        builder.HasIndex(x => x.Status);
    }
}
