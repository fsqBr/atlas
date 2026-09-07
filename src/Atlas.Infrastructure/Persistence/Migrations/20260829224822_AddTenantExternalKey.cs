using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantExternalKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_key",
                schema: "atlas",
                table: "tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.UpdateData(
                schema: "atlas",
                table: "tenants",
                keyColumn: "id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "external_key",
                value: null);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_external_key",
                schema: "atlas",
                table: "tenants",
                column: "external_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenants_external_key",
                schema: "atlas",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "external_key",
                schema: "atlas",
                table: "tenants");
        }
    }
}
