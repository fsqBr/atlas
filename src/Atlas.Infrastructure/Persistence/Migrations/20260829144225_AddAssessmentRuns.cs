using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssessmentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                schema: "atlas",
                table: "scans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                schema: "atlas",
                table: "inventory_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                schema: "atlas",
                table: "health_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assessment_runs",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    scanners_run = table.Column<int>(type: "integer", nullable: false),
                    scanners_failed = table.Column<int>(type: "integer", nullable: false),
                    findings_new = table.Column<int>(type: "integer", nullable: false),
                    findings_recurring = table.Column<int>(type: "integer", nullable: false),
                    findings_resolved = table.Column<int>(type: "integer", nullable: false),
                    findings_regressed = table.Column<int>(type: "integer", nullable: false),
                    open_findings = table.Column<int>(type: "integer", nullable: true),
                    health_score = table.Column<int>(type: "integer", nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assessment_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_assessment_runs_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assessment_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scans_run_id",
                schema: "atlas",
                table: "scans",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_inventory_snapshots_run_id",
                schema: "atlas",
                table: "inventory_snapshots",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_snapshots_run_id",
                schema: "atlas",
                table: "health_snapshots",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_assessment_runs_assessment_id_number",
                schema: "atlas",
                table: "assessment_runs",
                columns: new[] { "assessment_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assessment_runs_tenant_id",
                schema: "atlas",
                table: "assessment_runs",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_health_snapshots_assessment_runs_run_id",
                schema: "atlas",
                table: "health_snapshots",
                column: "run_id",
                principalSchema: "atlas",
                principalTable: "assessment_runs",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_inventory_snapshots_assessment_runs_run_id",
                schema: "atlas",
                table: "inventory_snapshots",
                column: "run_id",
                principalSchema: "atlas",
                principalTable: "assessment_runs",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_scans_assessment_runs_run_id",
                schema: "atlas",
                table: "scans",
                column: "run_id",
                principalSchema: "atlas",
                principalTable: "assessment_runs",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_health_snapshots_assessment_runs_run_id",
                schema: "atlas",
                table: "health_snapshots");

            migrationBuilder.DropForeignKey(
                name: "fk_inventory_snapshots_assessment_runs_run_id",
                schema: "atlas",
                table: "inventory_snapshots");

            migrationBuilder.DropForeignKey(
                name: "fk_scans_assessment_runs_run_id",
                schema: "atlas",
                table: "scans");

            migrationBuilder.DropTable(
                name: "assessment_runs",
                schema: "atlas");

            migrationBuilder.DropIndex(
                name: "ix_scans_run_id",
                schema: "atlas",
                table: "scans");

            migrationBuilder.DropIndex(
                name: "ix_inventory_snapshots_run_id",
                schema: "atlas",
                table: "inventory_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_health_snapshots_run_id",
                schema: "atlas",
                table: "health_snapshots");

            migrationBuilder.DropColumn(
                name: "run_id",
                schema: "atlas",
                table: "scans");

            migrationBuilder.DropColumn(
                name: "run_id",
                schema: "atlas",
                table: "inventory_snapshots");

            migrationBuilder.DropColumn(
                name: "run_id",
                schema: "atlas",
                table: "health_snapshots");
        }
    }
}
