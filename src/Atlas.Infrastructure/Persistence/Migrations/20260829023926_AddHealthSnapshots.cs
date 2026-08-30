using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_snapshots",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    risk_level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    open_findings = table.Column<int>(type: "integer", nullable: false),
                    project_count = table.Column<int>(type: "integer", nullable: false),
                    dimensions_json = table.Column<string>(type: "jsonb", nullable: false),
                    explanation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_health_snapshots_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_health_snapshots_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_health_snapshots_assessment_id_created_at_utc",
                schema: "atlas",
                table: "health_snapshots",
                columns: new[] { "assessment_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_health_snapshots_tenant_id",
                schema: "atlas",
                table: "health_snapshots",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "health_snapshots",
                schema: "atlas");
        }
    }
}
