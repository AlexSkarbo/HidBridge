using System;
using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HidBridge.Persistence.Sql.Migrations.Migrations;

/// <summary>
/// Extends the command journal schema with agent, collaboration, and completion metadata.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260303002000_AddCommandJournalCollaborationColumns")]
public partial class AddCommandJournalCollaborationColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AgentId",
            schema: "hidbridge",
            table: "command_journal",
            type: "character varying(128)",
            maxLength: 128,
            nullable: false,
            defaultValue: string.Empty);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "CompletedAtUtc",
            schema: "hidbridge",
            table: "command_journal",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MetricsJson",
            schema: "hidbridge",
            table: "command_journal",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ParticipantId",
            schema: "hidbridge",
            table: "command_journal",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PrincipalId",
            schema: "hidbridge",
            table: "command_journal",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ShareId",
            schema: "hidbridge",
            table: "command_journal",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_command_journal_AgentId",
            schema: "hidbridge",
            table: "command_journal",
            column: "AgentId");

        migrationBuilder.CreateIndex(
            name: "IX_command_journal_ParticipantId",
            schema: "hidbridge",
            table: "command_journal",
            column: "ParticipantId");

        migrationBuilder.CreateIndex(
            name: "IX_command_journal_ShareId",
            schema: "hidbridge",
            table: "command_journal",
            column: "ShareId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_command_journal_AgentId",
            schema: "hidbridge",
            table: "command_journal");

        migrationBuilder.DropIndex(
            name: "IX_command_journal_ParticipantId",
            schema: "hidbridge",
            table: "command_journal");

        migrationBuilder.DropIndex(
            name: "IX_command_journal_ShareId",
            schema: "hidbridge",
            table: "command_journal");

        migrationBuilder.DropColumn(
            name: "AgentId",
            schema: "hidbridge",
            table: "command_journal");

        migrationBuilder.DropColumn(
            name: "CompletedAtUtc",
            schema: "hidbridge",
            table: "command_journal");

        migrationBuilder.DropColumn(
            name: "MetricsJson",
            schema: "hidbridge",
            table: "command_journal");

        migrationBuilder.DropColumn(
            name: "ParticipantId",
            schema: "hidbridge",
            table: "command_journal");

        migrationBuilder.DropColumn(
            name: "PrincipalId",
            schema: "hidbridge",
            table: "command_journal");

        migrationBuilder.DropColumn(
            name: "ShareId",
            schema: "hidbridge",
            table: "command_journal");
    }
}
