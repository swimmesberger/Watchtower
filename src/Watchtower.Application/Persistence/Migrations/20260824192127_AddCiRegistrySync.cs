using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCiRegistrySync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_registry_sync_error",
                table: "ci_repos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "registry_synced_at",
                table: "ci_repos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "registry_synced_hash",
                table: "ci_repos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_registry_url",
                table: "ci_repos",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_registry_sync_error",
                table: "ci_repos");

            migrationBuilder.DropColumn(
                name: "registry_synced_at",
                table: "ci_repos");

            migrationBuilder.DropColumn(
                name: "registry_synced_hash",
                table: "ci_repos");

            migrationBuilder.DropColumn(
                name: "sync_registry_url",
                table: "ci_repos");
        }
    }
}
