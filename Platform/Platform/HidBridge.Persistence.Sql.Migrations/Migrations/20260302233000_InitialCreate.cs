using System;
using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HidBridge.Persistence.Sql.Migrations.Migrations;

/// <summary>
/// Creates the initial relational schema for the HidBridge control plane.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260302233000_InitialCreate")]
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "hidbridge");

        migrationBuilder.CreateTable(
            name: "agents",
            schema: "hidbridge",
            columns: table => new
            {
                AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                EndpointId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ConnectorType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agents", x => x.AgentId);
            });

        migrationBuilder.CreateTable(
            name: "audit_events",
            schema: "hidbridge",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                DataJson = table.Column<string>(type: "text", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_events", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "command_journal",
            schema: "hidbridge",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                CommandId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Channel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ArgsJson = table.Column<string>(type: "text", nullable: false),
                TimeoutMs = table.Column<int>(type: "integer", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ErrorJson = table.Column<string>(type: "text", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_command_journal", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "endpoints",
            schema: "hidbridge",
            columns: table => new
            {
                EndpointId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_endpoints", x => x.EndpointId);
            });

        migrationBuilder.CreateTable(
            name: "sessions",
            schema: "hidbridge",
            columns: table => new
            {
                SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                EndpointId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Profile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                State = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sessions", x => x.SessionId);
            });

        migrationBuilder.CreateTable(
            name: "session_participants",
            schema: "hidbridge",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ParticipantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                JoinedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_session_participants", x => x.Id);
                table.ForeignKey(
                    name: "FK_session_participants_sessions_SessionId",
                    column: x => x.SessionId,
                    principalSchema: "hidbridge",
                    principalTable: "sessions",
                    principalColumn: "SessionId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "session_shares",
            schema: "hidbridge",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ShareId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                GrantedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_session_shares", x => x.Id);
                table.ForeignKey(
                    name: "FK_session_shares_sessions_SessionId",
                    column: x => x.SessionId,
                    principalSchema: "hidbridge",
                    principalTable: "sessions",
                    principalColumn: "SessionId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "telemetry_events",
            schema: "hidbridge",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Scope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                MetricsJson = table.Column<string>(type: "text", nullable: false),
                SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_telemetry_events", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "agent_capabilities",
            schema: "hidbridge",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                AgentId = table.Column<string>(type: "character varying(128)", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                MetadataJson = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_capabilities", x => x.Id);
                table.ForeignKey(
                    name: "FK_agent_capabilities_agents_AgentId",
                    column: x => x.AgentId,
                    principalSchema: "hidbridge",
                    principalTable: "agents",
                    principalColumn: "AgentId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "endpoint_capabilities",
            schema: "hidbridge",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EndpointId = table.Column<string>(type: "character varying(128)", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                MetadataJson = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_endpoint_capabilities", x => x.Id);
                table.ForeignKey(
                    name: "FK_endpoint_capabilities_endpoints_EndpointId",
                    column: x => x.EndpointId,
                    principalSchema: "hidbridge",
                    principalTable: "endpoints",
                    principalColumn: "EndpointId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_agent_capabilities_AgentId_Name",
            schema: "hidbridge",
            table: "agent_capabilities",
            columns: new[] { "AgentId", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_agents_EndpointId",
            schema: "hidbridge",
            table: "agents",
            column: "EndpointId");

        migrationBuilder.CreateIndex(
            name: "IX_audit_events_Category",
            schema: "hidbridge",
            table: "audit_events",
            column: "Category");

        migrationBuilder.CreateIndex(
            name: "IX_audit_events_CreatedAtUtc",
            schema: "hidbridge",
            table: "audit_events",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_audit_events_SessionId",
            schema: "hidbridge",
            table: "audit_events",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "IX_command_journal_CommandId",
            schema: "hidbridge",
            table: "command_journal",
            column: "CommandId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_command_journal_CreatedAtUtc",
            schema: "hidbridge",
            table: "command_journal",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_command_journal_IdempotencyKey",
            schema: "hidbridge",
            table: "command_journal",
            column: "IdempotencyKey");

        migrationBuilder.CreateIndex(
            name: "IX_command_journal_SessionId",
            schema: "hidbridge",
            table: "command_journal",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "IX_endpoint_capabilities_EndpointId_Name",
            schema: "hidbridge",
            table: "endpoint_capabilities",
            columns: new[] { "EndpointId", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_sessions_AgentId",
            schema: "hidbridge",
            table: "sessions",
            column: "AgentId");

        migrationBuilder.CreateIndex(
            name: "IX_sessions_EndpointId",
            schema: "hidbridge",
            table: "sessions",
            column: "EndpointId");

        migrationBuilder.CreateIndex(
            name: "IX_sessions_LeaseExpiresAtUtc",
            schema: "hidbridge",
            table: "sessions",
            column: "LeaseExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_sessions_State",
            schema: "hidbridge",
            table: "sessions",
            column: "State");

        migrationBuilder.CreateIndex(
            name: "IX_session_participants_PrincipalId",
            schema: "hidbridge",
            table: "session_participants",
            column: "PrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_session_participants_SessionId_ParticipantId",
            schema: "hidbridge",
            table: "session_participants",
            columns: new[] { "SessionId", "ParticipantId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_session_shares_PrincipalId",
            schema: "hidbridge",
            table: "session_shares",
            column: "PrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_session_shares_SessionId_ShareId",
            schema: "hidbridge",
            table: "session_shares",
            columns: new[] { "SessionId", "ShareId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_session_shares_Status",
            schema: "hidbridge",
            table: "session_shares",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_telemetry_events_CreatedAtUtc",
            schema: "hidbridge",
            table: "telemetry_events",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_telemetry_events_Scope",
            schema: "hidbridge",
            table: "telemetry_events",
            column: "Scope");

        migrationBuilder.CreateIndex(
            name: "IX_telemetry_events_SessionId",
            schema: "hidbridge",
            table: "telemetry_events",
            column: "SessionId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "agent_capabilities",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "audit_events",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "command_journal",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "endpoint_capabilities",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "session_participants",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "session_shares",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "sessions",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "telemetry_events",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "agents",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "endpoints",
            schema: "hidbridge");
    }
}
