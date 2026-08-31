using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atlas.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantNotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_notification_settings",
                schema: "atlas",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    webhook_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    secret = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    slack_webhook_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    teams_webhook_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    digest_day_of_week = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    digest_hour_utc = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_notification_settings", x => x.tenant_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_notification_settings",
                schema: "atlas");
        }
    }
}
