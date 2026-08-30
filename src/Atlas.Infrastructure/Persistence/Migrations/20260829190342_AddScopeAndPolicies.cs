using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScopeAndPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "exclude_globs_json",
                schema: "atlas",
                table: "assessments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "suppression_policies",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rule_pattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    path_glob = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    author = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suppression_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_suppression_policies_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_suppression_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_suppression_policies_assessment_id",
                schema: "atlas",
                table: "suppression_policies",
                column: "assessment_id");

            migrationBuilder.CreateIndex(
                name: "ix_suppression_policies_tenant_id_assessment_id",
                schema: "atlas",
                table: "suppression_policies",
                columns: new[] { "tenant_id", "assessment_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "suppression_policies",
                schema: "atlas");

            migrationBuilder.DropColumn(
                name: "exclude_globs_json",
                schema: "atlas",
                table: "assessments");
        }
    }
}
