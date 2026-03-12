using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HidBridge.Persistence.Sql.Migrations.Migrations;

/// <inheritdoc />
[DbContext(typeof(PlatformDbContext))]
[Migration("20260304080000_AddPolicyScopeActivation")]
public partial class AddPolicyScopeActivation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsActive",
            schema: "hidbridge",
            table: "policy_scopes",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.CreateIndex(
            name: "IX_policy_scopes_IsActive",
            schema: "hidbridge",
            table: "policy_scopes",
            column: "IsActive");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_policy_scopes_IsActive",
            schema: "hidbridge",
            table: "policy_scopes");

        migrationBuilder.DropColumn(
            name: "IsActive",
            schema: "hidbridge",
            table: "policy_scopes");
    }
}
