using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Governance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_prices",
                schema: "governance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    output = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    cache_read = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    cache_write = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_prices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_prices_tenant_id_pattern",
                schema: "governance",
                table: "model_prices",
                columns: new[] { "tenant_id", "pattern" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_prices",
                schema: "governance");
        }
    }
}
