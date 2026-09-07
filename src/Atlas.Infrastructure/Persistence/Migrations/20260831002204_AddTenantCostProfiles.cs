using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCostProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at_utc",
                schema: "atlas",
                table: "suppression_policies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at_utc",
                schema: "atlas",
                table: "finding_suppressions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tags_json",
                schema: "atlas",
                table: "assessments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_cost_profiles",
                schema: "atlas",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    hourly_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    team_size = table.Column<int>(type: "integer", nullable: true),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_cost_profiles", x => x.tenant_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_cost_profiles",
                schema: "atlas");

            migrationBuilder.DropColumn(
                name: "expires_at_utc",
                schema: "atlas",
                table: "suppression_policies");

            migrationBuilder.DropColumn(
                name: "expires_at_utc",
                schema: "atlas",
                table: "finding_suppressions");

            migrationBuilder.DropColumn(
                name: "tags_json",
                schema: "atlas",
                table: "assessments");
        }
    }
}
