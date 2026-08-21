using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStackBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "backup_enabled",
                table: "stacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // defaultValue backfills EXISTING rows: stop-during-backup defaults to on (the entity
            // initializer), so pre-existing stacks get the same behaviour a new stack would.
            migrationBuilder.AddColumn<bool>(
                name: "backup_stop_containers",
                table: "stacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "backup_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    stack_id = table.Column<int>(type: "INTEGER", nullable: false),
                    triggered_by = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    remote_path = table.Column<string>(type: "TEXT", nullable: true),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    output = table.Column<string>(type: "TEXT", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_backup_events_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_events_stack_id_started_at",
                table: "backup_events",
                columns: new[] { "stack_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_events_status",
                table: "backup_events",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_events");

            migrationBuilder.DropColumn(
                name: "backup_enabled",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "backup_stop_containers",
                table: "stacks");
        }
    }
}
