using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdoptElarionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.CreateTable(
                name: "elarion_settings",
                columns: table => new
                {
                    kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    owner = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: true),
                    updated_on_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_elarion_settings", x => new { x.kind, x.owner, x.key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "elarion_settings");

            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_settings", x => x.key);
                });
        }
    }
}
