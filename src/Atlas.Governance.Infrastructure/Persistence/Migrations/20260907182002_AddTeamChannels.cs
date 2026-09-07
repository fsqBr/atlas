using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Governance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "slack_webhook_url",
                schema: "governance",
                table: "teams",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "teams_webhook_url",
                schema: "governance",
                table: "teams",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "webhook_url",
                schema: "governance",
                table: "teams",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "slack_webhook_url",
                schema: "governance",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "teams_webhook_url",
                schema: "governance",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "webhook_url",
                schema: "governance",
                table: "teams");
        }
    }
}
