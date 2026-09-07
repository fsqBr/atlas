using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Governance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usage_facts",
                schema: "governance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    tool = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    period = table.Column<DateOnly>(type: "date", nullable: false),
                    model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_write_tokens = table.Column<long>(type: "bigint", nullable: false),
                    requests = table.Column<int>(type: "integer", nullable: false),
                    sessions = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    price_catalog_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reported_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_facts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_facts_tenant_id_actor_tool_period_model",
                schema: "governance",
                table: "usage_facts",
                columns: new[] { "tenant_id", "actor", "tool", "period", "model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_facts_tenant_id_period",
                schema: "governance",
                table: "usage_facts",
                columns: new[] { "tenant_id", "period" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_facts",
                schema: "governance");
        }
    }
}
