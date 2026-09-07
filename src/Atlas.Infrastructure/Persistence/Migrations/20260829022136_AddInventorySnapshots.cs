using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventorySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventory_snapshots",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    language_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    tier_achieved = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    file_count = table.Column<int>(type: "integer", nullable: false),
                    total_lines = table.Column<long>(type: "bigint", nullable: false),
                    type_count = table.Column<int>(type: "integer", nullable: false),
                    method_count = table.Column<int>(type: "integer", nullable: false),
                    max_cyclomatic_complexity = table.Column<int>(type: "integer", nullable: false),
                    average_cyclomatic_complexity = table.Column<double>(type: "double precision", nullable: false),
                    symbol_resolution_rate = table.Column<double>(type: "double precision", nullable: true),
                    project_count = table.Column<int>(type: "integer", nullable: false),
                    solution_count = table.Column<int>(type: "integer", nullable: false),
                    projects_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_inventory_snapshots_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_inventory_snapshots_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_snapshots_assessment_id_language_id_created_at_utc",
                schema: "atlas",
                table: "inventory_snapshots",
                columns: new[] { "assessment_id", "language_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_snapshots_tenant_id",
                schema: "atlas",
                table: "inventory_snapshots",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_snapshots",
                schema: "atlas");
        }
    }
}
