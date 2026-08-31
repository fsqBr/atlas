using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleAndWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rerun_every_days",
                schema: "atlas",
                table: "assessments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "webhook_url",
                schema: "atlas",
                table: "assessments",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rerun_every_days",
                schema: "atlas",
                table: "assessments");

            migrationBuilder.DropColumn(
                name: "webhook_url",
                schema: "atlas",
                table: "assessments");
        }
    }
}
