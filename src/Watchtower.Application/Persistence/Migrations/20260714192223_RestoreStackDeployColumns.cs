using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestoreStackDeployColumns : Migration
    {
        // The reverse-proxy migrations (AddRoutes / AddStackTemplates) were scaffolded on a branch
        // whose model predated PullBasedDeployment, so their snapshots never learned about the
        // auto_deploy_mode / auto_deploy_time / last_deployed_commit columns. AddStackTemplates adds
        // the template_id foreign key, which on SQLite forces a full "stacks" table rebuild; the
        // rebuild recreates the table from that stale snapshot and silently drops the three columns.
        // Because AddStackTemplates drops them on every database (fresh or already-migrated), they are
        // always absent by the time this migration runs, so a plain AddColumn safely restores them.

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
        }
    }
}
