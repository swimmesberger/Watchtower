using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PullBasedDeployment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows must read back as a parseable enum name, not "".
            migrationBuilder.AddColumn<string>(
                name: "auto_deploy_mode",
                table: "stacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.AddColumn<string>(
                name: "auto_deploy_time",
                table: "stacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_deployed_commit",
                table: "stacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "new_commit_sha",
                table: "stack_update_checks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_deploy_mode",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "auto_deploy_time",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "last_deployed_commit",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "new_commit_sha",
                table: "stack_update_checks");
        }
    }
}
