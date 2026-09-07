using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssessmentsAndFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assessments",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_locator = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    branch = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assessments", x => x.id);
                    table.ForeignKey(
                        name: "fk_assessments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rule_definitions",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scanner_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    default_severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    remediation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scan_jobs",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    leased_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    queued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scan_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_scan_jobs_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scan_jobs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scans",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scanner_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scanner_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    findings_emitted = table.Column<int>(type: "integer", nullable: false),
                    findings_new = table.Column<int>(type: "integer", nullable: false),
                    findings_recurring = table.Column<int>(type: "integer", nullable: false),
                    findings_resolved = table.Column<int>(type: "integer", nullable: false),
                    findings_regressed = table.Column<int>(type: "integer", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scans", x => x.id);
                    table.ForeignKey(
                        name: "fk_scans_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scans_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "findings",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    first_seen_scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolved_scan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_findings_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_findings_rule_definitions_rule_id",
                        column: x => x.rule_id,
                        principalSchema: "atlas",
                        principalTable: "rule_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_findings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "finding_occurrences",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    finding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    confidence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    remediation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    evidence_file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    evidence_line_start = table.Column<int>(type: "integer", nullable: true),
                    evidence_line_end = table.Column<int>(type: "integer", nullable: true),
                    evidence_symbol = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    evidence_snippet_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    evidence_scanner_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    evidence_scanner_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    data_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_finding_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "fk_finding_occurrences_findings_finding_id",
                        column: x => x.finding_id,
                        principalSchema: "atlas",
                        principalTable: "findings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_finding_occurrences_scans_scan_id",
                        column: x => x.scan_id,
                        principalSchema: "atlas",
                        principalTable: "scans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_finding_occurrences_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assessments_tenant_id_created_at_utc",
                schema: "atlas",
                table: "assessments",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_finding_occurrences_finding_id_created_at_utc",
                schema: "atlas",
                table: "finding_occurrences",
                columns: new[] { "finding_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_finding_occurrences_scan_id",
                schema: "atlas",
                table: "finding_occurrences",
                column: "scan_id");

            migrationBuilder.CreateIndex(
                name: "ix_finding_occurrences_tenant_id",
                schema: "atlas",
                table: "finding_occurrences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_findings_assessment_id_rule_id_status",
                schema: "atlas",
                table: "findings",
                columns: new[] { "assessment_id", "rule_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_findings_rule_id",
                schema: "atlas",
                table: "findings",
                column: "rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_findings_tenant_id_assessment_id_fingerprint",
                schema: "atlas",
                table: "findings",
                columns: new[] { "tenant_id", "assessment_id", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scan_jobs_assessment_id",
                schema: "atlas",
                table: "scan_jobs",
                column: "assessment_id");

            migrationBuilder.CreateIndex(
                name: "ix_scan_jobs_state_queued_at_utc",
                schema: "atlas",
                table: "scan_jobs",
                columns: new[] { "state", "queued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_scan_jobs_tenant_id",
                schema: "atlas",
                table: "scan_jobs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_scans_assessment_id_scanner_id_commit_sha_status",
                schema: "atlas",
                table: "scans",
                columns: new[] { "assessment_id", "scanner_id", "commit_sha", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_scans_tenant_id",
                schema: "atlas",
                table: "scans",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "finding_occurrences",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "scan_jobs",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "findings",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "scans",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "rule_definitions",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "assessments",
                schema: "atlas");
        }
    }
}
