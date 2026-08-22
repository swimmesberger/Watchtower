using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupQuiesceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "backup_quiesce_mode",
                table: "stacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "Stop");

            migrationBuilder.CreateTable(
                name: "backup_paused_containers",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    container_id = table.Column<string>(type: "TEXT", nullable: false),
                    container_name = table.Column<string>(type: "TEXT", nullable: false),
                    stack_name = table.Column<string>(type: "TEXT", nullable: false),
                    paused_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_paused_containers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_paused_containers_container_id",
                table: "backup_paused_containers",
                column: "container_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_paused_containers");

            migrationBuilder.DropColumn(
                name: "backup_quiesce_mode",
                table: "stacks");
        }
    }
}
