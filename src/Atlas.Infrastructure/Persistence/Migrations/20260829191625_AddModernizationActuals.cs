using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModernizationActuals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "modernization_actuals",
                schema: "atlas",
                columns: table => new
                {
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    strategy = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actual_hours = table.Column<double>(type: "double precision", nullable: false),
                    actual_months = table.Column<double>(type: "double precision", nullable: true),
                    actual_cost = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    recorded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modernization_actuals", x => x.assessment_id);
                    table.ForeignKey(
                        name: "fk_modernization_actuals_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_modernization_actuals_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_modernization_actuals_tenant_id",
                schema: "atlas",
                table: "modernization_actuals",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modernization_actuals",
                schema: "atlas");
        }
    }
}
