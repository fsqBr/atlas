using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSavingsRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "extended_support_per_legacy_app_year",
                schema: "atlas",
                table: "tenant_cost_profiles",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "sql_server_savings_per_year",
                schema: "atlas",
                table: "tenant_cost_profiles",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "windows_hosting_per_legacy_app_year",
                schema: "atlas",
                table: "tenant_cost_profiles",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "extended_support_per_legacy_app_year",
                schema: "atlas",
                table: "tenant_cost_profiles");

            migrationBuilder.DropColumn(
                name: "sql_server_savings_per_year",
                schema: "atlas",
                table: "tenant_cost_profiles");

            migrationBuilder.DropColumn(
                name: "windows_hosting_per_legacy_app_year",
                schema: "atlas",
                table: "tenant_cost_profiles");
        }
    }
}
