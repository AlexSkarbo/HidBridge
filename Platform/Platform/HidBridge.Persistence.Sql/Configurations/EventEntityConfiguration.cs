using HidBridge.Persistence.Sql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HidBridge.Persistence.Sql.Configurations;

/// <summary>
/// Configures the persisted audit event table.
/// </summary>
internal sealed class AuditEventEntityConfiguration : IEntityTypeConfiguration<AuditEventEntity>
{
    /// <summary>
    /// Builds the EF Core mapping for audit events.
    /// </summary>
    public void Configure(EntityTypeBuilder<AuditEventEntity> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Category).HasMaxLength(128);
        builder.Property(x => x.Message).HasMaxLength(2048);
        builder.Property(x => x.SessionId).HasMaxLength(128);
        builder.HasIndex(x => x.CreatedAtUtc);
        builder.HasIndex(x => x.SessionId);
        builder.HasIndex(x => x.Category);
    }
}

/// <summary>
/// Configures the persisted telemetry event table.
/// </summary>
internal sealed class TelemetryEventEntityConfiguration : IEntityTypeConfiguration<TelemetryEventEntity>
{
    /// <summary>
    /// Builds the EF Core mapping for telemetry events.
    /// </summary>
    public void Configure(EntityTypeBuilder<TelemetryEventEntity> builder)
    {
        builder.ToTable("telemetry_events");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Scope).HasMaxLength(128);
        builder.Property(x => x.SessionId).HasMaxLength(128);
        builder.HasIndex(x => x.CreatedAtUtc);
        builder.HasIndex(x => x.SessionId);
        builder.HasIndex(x => x.Scope);
    }
}

/// <summary>
/// Configures the persisted command journal table.
/// </summary>
internal sealed class CommandJournalEntityConfiguration : IEntityTypeConfiguration<CommandJournalEntity>
{
    /// <summary>
    /// Builds the EF Core mapping for command journal entries.
    /// </summary>
    public void Configure(EntityTypeBuilder<CommandJournalEntity> builder)
    {
        builder.ToTable("command_journal");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CommandId).HasMaxLength(128);
        builder.Property(x => x.SessionId).HasMaxLength(128);
        builder.Property(x => x.AgentId).HasMaxLength(128);
        builder.Property(x => x.Channel).HasMaxLength(64);
        builder.Property(x => x.Action).HasMaxLength(128);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(128);
        builder.Property(x => x.Status).HasMaxLength(64);
        builder.Property(x => x.ParticipantId).HasMaxLength(128);
        builder.Property(x => x.PrincipalId).HasMaxLength(256);
        builder.Property(x => x.ShareId).HasMaxLength(128);
        builder.Property(x => x.ErrorCode).HasMaxLength(128);
        builder.HasIndex(x => x.CommandId).IsUnique();
        builder.HasIndex(x => x.SessionId);
        builder.HasIndex(x => x.AgentId);
        builder.HasIndex(x => x.ParticipantId);
        builder.HasIndex(x => x.ShareId);
        builder.HasIndex(x => x.IdempotencyKey);
        builder.HasIndex(x => x.CreatedAtUtc);
    }
}
