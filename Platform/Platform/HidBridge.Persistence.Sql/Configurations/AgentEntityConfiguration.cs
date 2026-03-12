using HidBridge.Persistence.Sql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HidBridge.Persistence.Sql.Configurations;

internal sealed class AgentEntityConfiguration : IEntityTypeConfiguration<AgentEntity>
{
    public void Configure(EntityTypeBuilder<AgentEntity> builder)
    {
        builder.ToTable("agents");
        builder.HasKey(x => x.AgentId);
        builder.Property(x => x.AgentId).HasMaxLength(128);
        builder.Property(x => x.EndpointId).HasMaxLength(128);
        builder.Property(x => x.ConnectorType).HasMaxLength(64);
        builder.HasIndex(x => x.EndpointId);
        builder.HasMany(x => x.Capabilities)
            .WithOne(x => x.Agent)
            .HasForeignKey(x => x.AgentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AgentCapabilityEntityConfiguration : IEntityTypeConfiguration<AgentCapabilityEntity>
{
    public void Configure(EntityTypeBuilder<AgentCapabilityEntity> builder)
    {
        builder.ToTable("agent_capabilities");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.Version).HasMaxLength(32);
        builder.HasIndex(x => new { x.AgentId, x.Name });
    }
}
