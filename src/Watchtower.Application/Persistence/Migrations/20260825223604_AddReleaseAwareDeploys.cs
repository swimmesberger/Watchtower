using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <summary>
    /// ADR-0026 decisions 4–6: <c>products.release_mode</c>, the two release references on
    /// <c>stacks</c>, <c>deploy_events.release_id</c>, and the release columns of
    /// <c>stack_update_checks</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and inert on upgrade, and deliberately so — this is the stage that changes deploy
    /// behaviour, and the whole back-compat contract rests on one column default:
    /// <c>release_mode DEFAULT 'Git'</c> puts every existing product (and every product created
    /// afterwards) in the mode where nothing about deploying, polling or auto-deploying differs from
    /// before ADR-0026. Every release-aware code path is gated on the other value. Nothing is
    /// backfilled: a product that already accumulated releases in stage 3 stays in <c>Git</c> mode until
    /// its <em>next</em> release flips it, which is the announced, audited transition rather than a
    /// silent one on upgrade.
    /// </para>
    /// <para>
    /// The two foreign keys on <c>stacks</c> differ on purpose. <c>pinned_release_id</c> is
    /// <c>RESTRICT</c>: deleting a release a stack pins would silently flip that stack back to
    /// latest-tracking, so the database refuses and <c>products.deleteRelease</c> refuses first with the
    /// stacks named. <c>last_deployed_release_id</c> and <c>deploy_events.release_id</c> are
    /// <c>SET NULL</c>: both are records of the past, and refusing to prune a release because something
    /// once deployed it would make retention impossible.
    /// </para>
    /// </remarks>
    public partial class AddReleaseAwareDeploys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "last_deployed_release_id",
                table: "stacks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pinned_release_id",
                table: "stacks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "available_release_id",
                table: "stack_update_checks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "available_release_version",
                table: "stack_update_checks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "drifted_containers",
                table: "stack_update_checks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "release_mode",
                table: "products",
                type: "text",
                nullable: false,
                defaultValue: "Git");

            migrationBuilder.AddColumn<int>(
                name: "release_id",
                table: "deploy_events",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_stacks_last_deployed_release_id",
                table: "stacks",
                column: "last_deployed_release_id");

            migrationBuilder.CreateIndex(
                name: "ix_stacks_pinned_release_id",
                table: "stacks",
                column: "pinned_release_id");

            migrationBuilder.CreateIndex(
                name: "ix_deploy_events_release_id",
                table: "deploy_events",
                column: "release_id");

            migrationBuilder.AddForeignKey(
                name: "fk_deploy_events_releases_release_id",
                table: "deploy_events",
                column: "release_id",
                principalTable: "releases",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_stacks_releases_last_deployed_release_id",
                table: "stacks",
                column: "last_deployed_release_id",
                principalTable: "releases",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_stacks_releases_pinned_release_id",
                table: "stacks",
                column: "pinned_release_id",
                principalTable: "releases",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_deploy_events_releases_release_id",
                table: "deploy_events");

            migrationBuilder.DropForeignKey(
                name: "fk_stacks_releases_last_deployed_release_id",
                table: "stacks");

            migrationBuilder.DropForeignKey(
                name: "fk_stacks_releases_pinned_release_id",
                table: "stacks");

            migrationBuilder.DropIndex(
                name: "ix_stacks_last_deployed_release_id",
                table: "stacks");

            migrationBuilder.DropIndex(
                name: "ix_stacks_pinned_release_id",
                table: "stacks");

            migrationBuilder.DropIndex(
                name: "ix_deploy_events_release_id",
                table: "deploy_events");

            migrationBuilder.DropColumn(
                name: "last_deployed_release_id",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "pinned_release_id",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "available_release_id",
                table: "stack_update_checks");

            migrationBuilder.DropColumn(
                name: "available_release_version",
                table: "stack_update_checks");

            migrationBuilder.DropColumn(
                name: "drifted_containers",
                table: "stack_update_checks");

            migrationBuilder.DropColumn(
                name: "release_mode",
                table: "products");

            migrationBuilder.DropColumn(
                name: "release_id",
                table: "deploy_events");
        }
    }
}
