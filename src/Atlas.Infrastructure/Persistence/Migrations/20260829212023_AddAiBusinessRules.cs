using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiBusinessRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "atlas",
                table: "scan_jobs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "scan");

            migrationBuilder.CreateTable(
                name: "ai_provider_settings",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    key_envelope = table.Column<byte[]>(type: "bytea", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    max_snippets_per_analysis = table.Column<int>(type: "integer", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_tested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_provider_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_provider_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "business_rule_analyses",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    candidates_found = table.Column<int>(type: "integer", nullable: false),
                    snippets_sent = table.Column<int>(type: "integer", nullable: false),
                    rules_found = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_business_rule_analyses", x => x.id);
                    table.ForeignKey(
                        name: "fk_business_rule_analyses_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_business_rule_analyses_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "business_rules",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    symbol = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    start_line = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description_en = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    description_pt = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    conditions_json = table.Column<string>(type: "jsonb", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_business_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_business_rules_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_business_rules_business_rule_analyses_analysis_id",
                        column: x => x.analysis_id,
                        principalSchema: "atlas",
                        principalTable: "business_rule_analyses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_business_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_provider_settings_tenant_id",
                schema: "atlas",
                table: "ai_provider_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_business_rule_analyses_assessment_id_started_at_utc",
                schema: "atlas",
                table: "business_rule_analyses",
                columns: new[] { "assessment_id", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_business_rule_analyses_tenant_id",
                schema: "atlas",
                table: "business_rule_analyses",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_rules_analysis_id",
                schema: "atlas",
                table: "business_rules",
                column: "analysis_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_rules_assessment_id",
                schema: "atlas",
                table: "business_rules",
                column: "assessment_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_rules_tenant_id",
                schema: "atlas",
                table: "business_rules",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_provider_settings",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "business_rules",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "business_rule_analyses",
                schema: "atlas");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "atlas",
                table: "scan_jobs");
        }
    }
}
