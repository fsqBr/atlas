using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "feedback_comment",
                schema: "atlas",
                table: "business_rules",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rated_at_utc",
                schema: "atlas",
                table: "business_rules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rated_by",
                schema: "atlas",
                table: "business_rules",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rating",
                schema: "atlas",
                table: "business_rules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "feedback_comment",
                schema: "atlas",
                table: "ai_narratives",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rated_at_utc",
                schema: "atlas",
                table: "ai_narratives",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rated_by",
                schema: "atlas",
                table: "ai_narratives",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rating",
                schema: "atlas",
                table: "ai_narratives",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "feedback_comment",
                schema: "atlas",
                table: "business_rules");

            migrationBuilder.DropColumn(
                name: "rated_at_utc",
                schema: "atlas",
                table: "business_rules");

            migrationBuilder.DropColumn(
                name: "rated_by",
                schema: "atlas",
                table: "business_rules");

            migrationBuilder.DropColumn(
                name: "rating",
                schema: "atlas",
                table: "business_rules");

            migrationBuilder.DropColumn(
                name: "feedback_comment",
                schema: "atlas",
                table: "ai_narratives");

            migrationBuilder.DropColumn(
                name: "rated_at_utc",
                schema: "atlas",
                table: "ai_narratives");

            migrationBuilder.DropColumn(
                name: "rated_by",
                schema: "atlas",
                table: "ai_narratives");

            migrationBuilder.DropColumn(
                name: "rating",
                schema: "atlas",
                table: "ai_narratives");
        }
    }
}
