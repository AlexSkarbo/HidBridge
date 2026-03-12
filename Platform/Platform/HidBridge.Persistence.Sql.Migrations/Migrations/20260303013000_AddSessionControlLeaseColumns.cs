using System;
using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HidBridge.Persistence.Sql.Migrations.Migrations;

/// <summary>
/// Extends persisted session snapshots with active-controller lease metadata.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260303013000_AddSessionControlLeaseColumns")]
public partial class AddSessionControlLeaseColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ActiveControllerParticipantId",
            schema: "hidbridge",
            table: "sessions",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActiveControllerPrincipalId",
            schema: "hidbridge",
            table: "sessions",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ControlGrantedBy",
            schema: "hidbridge",
            table: "sessions",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ControlGrantedAtUtc",
            schema: "hidbridge",
            table: "sessions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ControlLeaseExpiresAtUtc",
            schema: "hidbridge",
            table: "sessions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_sessions_ActiveControllerPrincipalId",
            schema: "hidbridge",
            table: "sessions",
            column: "ActiveControllerPrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_sessions_ControlLeaseExpiresAtUtc",
            schema: "hidbridge",
            table: "sessions",
            column: "ControlLeaseExpiresAtUtc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_sessions_ActiveControllerPrincipalId",
            schema: "hidbridge",
            table: "sessions");

        migrationBuilder.DropIndex(
            name: "IX_sessions_ControlLeaseExpiresAtUtc",
            schema: "hidbridge",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "ActiveControllerParticipantId",
            schema: "hidbridge",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "ActiveControllerPrincipalId",
            schema: "hidbridge",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "ControlGrantedBy",
            schema: "hidbridge",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "ControlGrantedAtUtc",
            schema: "hidbridge",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "ControlLeaseExpiresAtUtc",
            schema: "hidbridge",
            table: "sessions");
    }
}
