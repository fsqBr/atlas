using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssessmentAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assessment_access",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    subject_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    granted_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    granted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assessment_access", x => x.id);
                    table.ForeignKey(
                        name: "fk_assessment_access_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assessment_access_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assessment_access_assessment_id_subject",
                schema: "atlas",
                table: "assessment_access",
                columns: new[] { "assessment_id", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assessment_access_tenant_id",
                schema: "atlas",
                table: "assessment_access",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assessment_access",
                schema: "atlas");
        }
    }
}
