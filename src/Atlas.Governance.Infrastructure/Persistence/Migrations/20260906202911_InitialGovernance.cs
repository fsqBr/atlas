using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Governance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "governance");

            migrationBuilder.CreateTable(
                name: "cost_facts",
                schema: "governance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    basis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    period = table.Column<DateOnly>(type: "date", nullable: false),
                    dimension = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    dimension_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: true),
                    unit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    price_catalog_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    collected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cost_facts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cost_sources",
                schema: "governance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    credential_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_sync_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    last_sync_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_sync_facts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cost_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seat_facts",
                schema: "governance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    seat_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    plan = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    last_activity_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pending_cancellation = table.Column<bool>(type: "boolean", nullable: false),
                    collected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seat_facts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cost_facts_source_id_period",
                schema: "governance",
                table: "cost_facts",
                columns: new[] { "source_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ix_cost_facts_tenant_id_period",
                schema: "governance",
                table: "cost_facts",
                columns: new[] { "tenant_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ix_cost_sources_tenant_id_provider",
                schema: "governance",
                table: "cost_sources",
                columns: new[] { "tenant_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seat_facts_source_id_seat_key",
                schema: "governance",
                table: "seat_facts",
                columns: new[] { "source_id", "seat_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seat_facts_tenant_id",
                schema: "governance",
                table: "seat_facts",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cost_facts",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "cost_sources",
                schema: "governance");

            migrationBuilder.DropTable(
                name: "seat_facts",
                schema: "governance");
        }
    }
}
