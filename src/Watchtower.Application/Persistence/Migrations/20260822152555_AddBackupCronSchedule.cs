using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupCronSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "backup_cron",
                table: "stacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_scheduled_backup_at",
                table: "stacks",
                type: "TEXT",
                nullable: true);

            // Seed the scheduler's cursor (ADR-0018) from the history the old daily scheduler left behind:
            // the newest schedule-triggered run per stack. Without this, an upgrade shortly after today's
            // window would see "never scheduled" and — under the misfire grace — run that window a second
            // time. DateTimeOffset columns are ISO-8601 text with a fixed +00:00 offset, so MAX() is
            // chronological. Stacks with no scheduled run yet stay NULL, which the scheduler treats as
            // "no history" (the grace rule then decides, exactly as for a stack opted in today).
            migrationBuilder.Sql("""
                UPDATE stacks
                SET last_scheduled_backup_at = (
                    SELECT MAX(e.started_at)
                    FROM backup_events e
                    WHERE e.stack_id = stacks.id AND e.triggered_by = 'schedule'
                )
                WHERE backup_enabled = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "backup_cron",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "last_scheduled_backup_at",
                table: "stacks");
        }
    }
}
