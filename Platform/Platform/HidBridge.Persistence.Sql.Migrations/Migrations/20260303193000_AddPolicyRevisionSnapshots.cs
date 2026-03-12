using HidBridge.Persistence.Sql;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HidBridge.Persistence.Sql.Migrations.Migrations;

/// <summary>
/// Adds versioned policy revision snapshots for audit and diagnostics.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260303193000_AddPolicyRevisionSnapshots")]
public partial class AddPolicyRevisionSnapshots : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "policy_revisions",
            schema: "hidbridge",
            columns: table => new
            {
                RevisionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                EntityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                EntityId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ScopeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Version = table.Column<int>(type: "integer", nullable: false),
                SnapshotJson = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_policy_revisions", x => x.RevisionId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_policy_revisions_CreatedAtUtc",
            schema: "hidbridge",
            table: "policy_revisions",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_policy_revisions_EntityType",
            schema: "hidbridge",
            table: "policy_revisions",
            column: "EntityType");

        migrationBuilder.CreateIndex(
            name: "IX_policy_revisions_ScopeId",
            schema: "hidbridge",
            table: "policy_revisions",
            column: "ScopeId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "policy_revisions",
            schema: "hidbridge");
    }
}
