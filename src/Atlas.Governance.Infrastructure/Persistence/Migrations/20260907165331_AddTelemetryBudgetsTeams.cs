using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Governance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryBudgetsTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "provider",
                schema: "governance",
                table: "usage_facts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "reported_cost",
                schema: "governance",
                table: "usage_facts",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "budget_alerts",
                schema: "governance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    budget_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    percent = table.Column<decimal>(type: "numeric(9,1)", precision: 9, scale: 1, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered = table.Column<bool>(type: "boolean", nullable: false),
                    delivery_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_budget_alerts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "budgets",
                schema: "governance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scope_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    monthly_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_budgets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                schema: "governance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    members_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_streams",
                schema: "governance",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_value = table.Column<double>(type: "double precision", nullable: false),
                    start_time_unix_nano = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_streams", x => new { x.tenant_id, x.stream_key });
                });

            migrationBuilder.CreateIndex(
                name: "ix_budget_alerts_tenant_id_created_at_utc",
                schema: "governance",
                table: "budget_alerts",
                columns: new[] { "tenant_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_budget_alerts_tenant_id_key",
                schema: "governance",
                table: "budget_alerts",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_budgets_tenant_id",
                schema: "governance",
                table: "budgets",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_teams_tenant_id_name",
                schema: "governance",
                table: "teams",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_streams_updated_at_utc",
                schema: "governance",
                table: "telemetry_streams",
                column: "updated_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_alerts",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "budgets",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "teams",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "telemetry_streams",
                schema: "governance");

            migrationBuilder.DropColumn(
                name: "provider",
                schema: "governance",
                table: "usage_facts");

            migrationBuilder.DropColumn(
                name: "reported_cost",
                schema: "governance",
                table: "usage_facts");
        }
    }
}
