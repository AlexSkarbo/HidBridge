using HidBridge.Persistence.Sql.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HidBridge.Persistence.Sql.Configurations;

internal sealed class EndpointEntityConfiguration : IEntityTypeConfiguration<EndpointEntity>
{
    public void Configure(EntityTypeBuilder<EndpointEntity> builder)
    {
        builder.ToTable("endpoints");
        builder.HasKey(x => x.EndpointId);
        builder.Property(x => x.EndpointId).HasMaxLength(128);
        builder.HasMany(x => x.Capabilities)
            .WithOne(x => x.Endpoint)
            .HasForeignKey(x => x.EndpointId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EndpointCapabilityEntityConfiguration : IEntityTypeConfiguration<EndpointCapabilityEntity>
{
    public void Configure(EntityTypeBuilder<EndpointCapabilityEntity> builder)
    {
        builder.ToTable("endpoint_capabilities");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(128);
        builder.Property(x => x.Version).HasMaxLength(32);
        builder.HasIndex(x => new { x.EndpointId, x.Name });
    }
}
