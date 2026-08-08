using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStackAppApi : Migration
    {
        // Additive only: two AddColumns plus one CreateIndex, deliberately NOT a SQLite table
        // rebuild. A rebuild recreates "stacks" from the snapshot and has already silently dropped
        // columns from this table once (see RestoreStackDeployColumns).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue is true (not EF's scaffolded false) so stacks that already exist come out
            // of the migration with the App API enabled — matching Stack.AppApiEnabled's initializer
            // and the documented "enabled unless an operator turns it off" behavior. The model
            // carries no store default, so EF always writes the property explicitly on insert and
            // this column default only ever applies to the rows backfilled here.
            migrationBuilder.AddColumn<bool>(
                name: "app_api_enabled",
                table: "stacks",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "app_api_token",
                table: "stacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_stacks_app_api_token",
                table: "stacks",
                column: "app_api_token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_stacks_app_api_token",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "app_api_enabled",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "app_api_token",
                table: "stacks");
        }
    }
}
