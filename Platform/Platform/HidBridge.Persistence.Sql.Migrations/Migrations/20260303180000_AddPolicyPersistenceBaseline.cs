using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HidBridge.Persistence.Sql.Migrations.Migrations;

/// <summary>
/// Adds baseline policy scope and policy assignment tables.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260303180000_AddPolicyPersistenceBaseline")]
public partial class AddPolicyPersistenceBaseline : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "policy_scopes",
            schema: "hidbridge",
            columns: table => new
            {
                ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                OrganizationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ViewerRoleRequired = table.Column<bool>(type: "boolean", nullable: false),
                ModeratorOverrideEnabled = table.Column<bool>(type: "boolean", nullable: false),
                AdminOverrideEnabled = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_policy_scopes", x => x.ScopeId);
            });

        migrationBuilder.CreateTable(
            name: "policy_assignments",
            schema: "hidbridge",
            columns: table => new
            {
                AssignmentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                RolesJson = table.Column<string>(type: "text", nullable: false),
                Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_policy_assignments", x => x.AssignmentId);
                table.ForeignKey(
                    name: "FK_policy_assignments_policy_scopes_ScopeId",
                    column: x => x.ScopeId,
                    principalSchema: "hidbridge",
                    principalTable: "policy_scopes",
                    principalColumn: "ScopeId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_policy_assignments_PrincipalId",
            schema: "hidbridge",
            table: "policy_assignments",
            column: "PrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_policy_assignments_ScopeId",
            schema: "hidbridge",
            table: "policy_assignments",
            column: "ScopeId");

        migrationBuilder.CreateIndex(
            name: "IX_policy_scopes_OrganizationId",
            schema: "hidbridge",
            table: "policy_scopes",
            column: "OrganizationId");

        migrationBuilder.CreateIndex(
            name: "IX_policy_scopes_TenantId",
            schema: "hidbridge",
            table: "policy_scopes",
            column: "TenantId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "policy_assignments",
            schema: "hidbridge");

        migrationBuilder.DropTable(
            name: "policy_scopes",
            schema: "hidbridge");
    }
}
