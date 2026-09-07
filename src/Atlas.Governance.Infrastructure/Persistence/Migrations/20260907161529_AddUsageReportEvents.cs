using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Governance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageReportEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usage_report_events",
                schema: "governance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    tool = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    period = table.Column<DateOnly>(type: "date", nullable: false),
                    reported_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tokens_today = table.Column<long>(type: "bigint", nullable: false),
                    estimated_cost_today = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    requests_today = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_report_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_report_events_tenant_id_reported_at_utc",
                schema: "governance",
                table: "usage_report_events",
                columns: new[] { "tenant_id", "reported_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_report_events",
                schema: "governance");
        }
    }
}
