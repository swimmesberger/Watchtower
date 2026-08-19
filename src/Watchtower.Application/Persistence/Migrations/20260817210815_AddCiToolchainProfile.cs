using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCiToolchainProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_warm_error",
                table: "ci_repos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_warmed_at",
                table: "ci_repos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "toolchain_detected_at",
                table: "ci_repos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "toolchain_profile_json",
                table: "ci_repos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warmed_profile_hash",
                table: "ci_repos",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_warm_error",
                table: "ci_repos");

            migrationBuilder.DropColumn(
                name: "last_warmed_at",
                table: "ci_repos");

            migrationBuilder.DropColumn(
                name: "toolchain_detected_at",
                table: "ci_repos");

            migrationBuilder.DropColumn(
                name: "toolchain_profile_json",
                table: "ci_repos");

            migrationBuilder.DropColumn(
                name: "warmed_profile_hash",
                table: "ci_repos");
        }
    }
}
