using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HidBridge.Persistence.Sql.Migrations.Migrations;

/// <summary>
/// Adds persisted activation state for policy assignments.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260304062000_AddPolicyAssignmentActivation")]
public partial class AddPolicyAssignmentActivation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsActive",
            schema: "hidbridge",
            table: "policy_assignments",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.CreateIndex(
            name: "IX_policy_assignments_IsActive",
            schema: "hidbridge",
            table: "policy_assignments",
            column: "IsActive");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_policy_assignments_IsActive",
            schema: "hidbridge",
            table: "policy_assignments");

        migrationBuilder.DropColumn(
            name: "IsActive",
            schema: "hidbridge",
            table: "policy_assignments");
    }
}
