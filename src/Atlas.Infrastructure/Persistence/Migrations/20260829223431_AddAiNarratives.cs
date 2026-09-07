using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiNarratives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_narratives",
                schema: "atlas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    lang = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_narratives", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_narratives_assessments_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "atlas",
                        principalTable: "assessments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_narratives_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "atlas",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_narratives_assessment_id_kind_key_lang",
                schema: "atlas",
                table: "ai_narratives",
                columns: new[] { "assessment_id", "kind", "key", "lang" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_narratives_tenant_id",
                schema: "atlas",
                table: "ai_narratives",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_narratives",
                schema: "atlas");
        }
    }
}
