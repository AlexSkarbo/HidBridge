using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HidBridge.Persistence.Sql.Migrations.Migrations;

/// <summary>
/// Extends persisted session snapshots with tenant and organization scope columns.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260303124500_AddSessionScopeColumns")]
public partial class AddSessionScopeColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OrganizationId",
            schema: "hidbridge",
            table: "sessions",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TenantId",
            schema: "hidbridge",
            table: "sessions",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_sessions_OrganizationId",
            schema: "hidbridge",
            table: "sessions",
            column: "OrganizationId");

        migrationBuilder.CreateIndex(
            name: "IX_sessions_TenantId",
            schema: "hidbridge",
            table: "sessions",
            column: "TenantId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_sessions_OrganizationId",
            schema: "hidbridge",
            table: "sessions");

        migrationBuilder.DropIndex(
            name: "IX_sessions_TenantId",
            schema: "hidbridge",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "OrganizationId",
            schema: "hidbridge",
            table: "sessions");

        migrationBuilder.DropColumn(
            name: "TenantId",
            schema: "hidbridge",
            table: "sessions");
    }
}
