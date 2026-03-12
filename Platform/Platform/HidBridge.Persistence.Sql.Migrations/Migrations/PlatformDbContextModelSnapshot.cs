using System;
using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HidBridge.Persistence.Sql.Migrations.Migrations;

/// <summary>
/// Represents the current relational model snapshot for the HidBridge SQL persistence layer.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
public partial class PlatformDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasDefaultSchema("hidbridge")
            .HasAnnotation("ProductVersion", "10.0.0")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.AgentCapabilityEntity", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint")
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            b.Property<string>("AgentId")
                .IsRequired()
                .HasColumnType("character varying(128)");

            b.Property<string>("MetadataJson")
                .HasColumnType("text");

            b.Property<string>("Name")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("Version")
                .IsRequired()
                .HasMaxLength(32)
                .HasColumnType("character varying(32)");

            b.HasKey("Id");

            b.HasIndex("AgentId", "Name");

            b.ToTable("agent_capabilities", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.AgentEntity", b =>
        {
            b.Property<string>("AgentId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("ConnectorType")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("character varying(64)");

            b.Property<string>("EndpointId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("AgentId");

            b.HasIndex("EndpointId");

            b.ToTable("agents", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.AuditEventEntity", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint")
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            b.Property<string>("Category")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("DataJson")
                .HasColumnType("text");

            b.Property<string>("Message")
                .IsRequired()
                .HasMaxLength(2048)
                .HasColumnType("character varying(2048)");

            b.Property<string>("SessionId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.HasKey("Id");

            b.HasIndex("Category");

            b.HasIndex("CreatedAtUtc");

            b.HasIndex("SessionId");

            b.ToTable("audit_events", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.CommandJournalEntity", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint")
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            b.Property<string>("Action")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("AgentId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("ArgsJson")
                .IsRequired()
                .HasColumnType("text");

            b.Property<string>("Channel")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("character varying(64)");

            b.Property<string>("CommandId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<DateTimeOffset?>("CompletedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("ErrorCode")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("ErrorJson")
                .HasColumnType("text");

            b.Property<string>("IdempotencyKey")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("MetricsJson")
                .HasColumnType("text");

            b.Property<string>("ParticipantId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("PrincipalId")
                .HasMaxLength(256)
                .HasColumnType("character varying(256)");

            b.Property<string>("SessionId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("ShareId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("Status")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("character varying(64)");

            b.Property<int>("TimeoutMs")
                .HasColumnType("integer");

            b.HasKey("Id");

            b.HasIndex("CommandId")
                .IsUnique();

            b.HasIndex("AgentId");

            b.HasIndex("CreatedAtUtc");

            b.HasIndex("IdempotencyKey");

            b.HasIndex("ParticipantId");

            b.HasIndex("SessionId");

            b.HasIndex("ShareId");

            b.ToTable("command_journal", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.EndpointCapabilityEntity", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint")
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            b.Property<string>("EndpointId")
                .IsRequired()
                .HasColumnType("character varying(128)");

            b.Property<string>("MetadataJson")
                .HasColumnType("text");

            b.Property<string>("Name")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("Version")
                .IsRequired()
                .HasMaxLength(32)
                .HasColumnType("character varying(32)");

            b.HasKey("Id");

            b.HasIndex("EndpointId", "Name");

            b.ToTable("endpoint_capabilities", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.EndpointEntity", b =>
        {
            b.Property<string>("EndpointId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("EndpointId");

            b.ToTable("endpoints", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.PolicyAssignmentEntity", b =>
        {
            b.Property<string>("AssignmentId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<bool>("IsActive")
                .ValueGeneratedOnAdd()
                .HasColumnType("boolean")
                .HasDefaultValue(true);

            b.Property<string>("PrincipalId")
                .IsRequired()
                .HasMaxLength(256)
                .HasColumnType("character varying(256)");

            b.Property<string>("RolesJson")
                .IsRequired()
                .HasColumnType("text");

            b.Property<string>("ScopeId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("Source")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("AssignmentId");

            b.HasIndex("IsActive");

            b.HasIndex("PrincipalId");

            b.HasIndex("ScopeId");

            b.ToTable("policy_assignments", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.PolicyScopeEntity", b =>
        {
            b.Property<string>("ScopeId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<bool>("AdminOverrideEnabled")
                .HasColumnType("boolean");

            b.Property<bool>("IsActive")
                .ValueGeneratedOnAdd()
                .HasColumnType("boolean")
                .HasDefaultValue(true);

            b.Property<bool>("ModeratorOverrideEnabled")
                .HasColumnType("boolean");

            b.Property<string>("OrganizationId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("TenantId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<bool>("ViewerRoleRequired")
                .HasColumnType("boolean");

            b.HasKey("ScopeId");

            b.HasIndex("OrganizationId");

            b.HasIndex("IsActive");

            b.HasIndex("TenantId");

            b.ToTable("policy_scopes", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.PolicyRevisionEntity", b =>
        {
            b.Property<string>("RevisionId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("EntityId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("EntityType")
                .IsRequired()
                .HasMaxLength(32)
                .HasColumnType("character varying(32)");

            b.Property<string>("PrincipalId")
                .HasMaxLength(256)
                .HasColumnType("character varying(256)");

            b.Property<string>("ScopeId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("SnapshotJson")
                .IsRequired()
                .HasColumnType("text");

            b.Property<string>("Source")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<int>("Version")
                .HasColumnType("integer");

            b.HasKey("RevisionId");

            b.HasIndex("CreatedAtUtc");

            b.HasIndex("EntityType");

            b.HasIndex("ScopeId");

            b.ToTable("policy_revisions", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.SessionEntity", b =>
        {
            b.Property<string>("SessionId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("AgentId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("EndpointId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("ActiveControllerParticipantId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("ActiveControllerPrincipalId")
                .HasMaxLength(256)
                .HasColumnType("character varying(256)");

            b.Property<string>("ControlGrantedBy")
                .HasMaxLength(256)
                .HasColumnType("character varying(256)");

            b.Property<DateTimeOffset?>("ControlGrantedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<DateTimeOffset?>("ControlLeaseExpiresAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<DateTimeOffset?>("LastHeartbeatAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<DateTimeOffset?>("LeaseExpiresAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("Profile")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("character varying(64)");

            b.Property<string>("OrganizationId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("RequestedBy")
                .IsRequired()
                .HasMaxLength(256)
                .HasColumnType("character varying(256)");

            b.Property<string>("Role")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("character varying(64)");

            b.Property<string>("State")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("character varying(64)");

            b.Property<string>("TenantId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("SessionId");

            b.HasIndex("AgentId");

            b.HasIndex("ActiveControllerPrincipalId");

            b.HasIndex("ControlLeaseExpiresAtUtc");

            b.HasIndex("EndpointId");

            b.HasIndex("LeaseExpiresAtUtc");

            b.HasIndex("OrganizationId");

            b.HasIndex("State");

            b.HasIndex("TenantId");

            b.ToTable("sessions", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.SessionParticipantEntity", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint")
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            b.Property<DateTimeOffset>("JoinedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("ParticipantId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("PrincipalId")
                .IsRequired()
                .HasMaxLength(256)
                .HasColumnType("character varying(256)");

            b.Property<string>("Role")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("character varying(64)");

            b.Property<string>("SessionId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("Id");

            b.HasIndex("PrincipalId");

            b.HasIndex("SessionId", "ParticipantId")
                .IsUnique();

            b.ToTable("session_participants", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.SessionShareEntity", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint")
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("GrantedBy")
                .IsRequired()
                .HasMaxLength(256)
                .HasColumnType("character varying(256)");

            b.Property<string>("PrincipalId")
                .IsRequired()
                .HasMaxLength(256)
                .HasColumnType("character varying(256)");

            b.Property<string>("Role")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("character varying(64)");

            b.Property<string>("SessionId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("ShareId")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("Status")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("character varying(64)");

            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("Id");

            b.HasIndex("PrincipalId");

            b.HasIndex("SessionId", "ShareId")
                .IsUnique();

            b.HasIndex("Status");

            b.ToTable("session_shares", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.TelemetryEventEntity", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint")
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("MetricsJson")
                .IsRequired()
                .HasColumnType("text");

            b.Property<string>("Scope")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.Property<string>("SessionId")
                .HasMaxLength(128)
                .HasColumnType("character varying(128)");

            b.HasKey("Id");

            b.HasIndex("CreatedAtUtc");

            b.HasIndex("Scope");

            b.HasIndex("SessionId");

            b.ToTable("telemetry_events", "hidbridge");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.AgentCapabilityEntity", b =>
        {
            b.HasOne("HidBridge.Persistence.Sql.Entities.AgentEntity", "Agent")
                .WithMany("Capabilities")
                .HasForeignKey("AgentId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Agent");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.EndpointCapabilityEntity", b =>
        {
            b.HasOne("HidBridge.Persistence.Sql.Entities.EndpointEntity", "Endpoint")
                .WithMany("Capabilities")
                .HasForeignKey("EndpointId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Endpoint");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.PolicyAssignmentEntity", b =>
        {
            b.HasOne("HidBridge.Persistence.Sql.Entities.PolicyScopeEntity", "Scope")
                .WithMany("Assignments")
                .HasForeignKey("ScopeId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Scope");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.SessionParticipantEntity", b =>
        {
            b.HasOne("HidBridge.Persistence.Sql.Entities.SessionEntity", "Session")
                .WithMany("Participants")
                .HasForeignKey("SessionId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Session");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.SessionShareEntity", b =>
        {
            b.HasOne("HidBridge.Persistence.Sql.Entities.SessionEntity", "Session")
                .WithMany("Shares")
                .HasForeignKey("SessionId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Session");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.AgentEntity", b =>
        {
            b.Navigation("Capabilities");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.EndpointEntity", b =>
        {
            b.Navigation("Capabilities");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.PolicyScopeEntity", b =>
        {
            b.Navigation("Assignments");
        });

        modelBuilder.Entity("HidBridge.Persistence.Sql.Entities.SessionEntity", b =>
        {
            b.Navigation("Participants");

            b.Navigation("Shares");
        });
#pragma warning restore 612, 618
    }
}
