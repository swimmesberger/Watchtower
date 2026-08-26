using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <summary>
    /// docs/products/design.md §"Secret sync": the release contributor's state on <c>products</c>
    /// (<c>sync_release_secrets</c>, <c>actions_synced_hash</c>, <c>actions_synced_at</c>,
    /// <c>last_actions_sync_error</c>) plus the filtered unique index that makes the monorepo conflict
    /// unrepresentable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and inert on upgrade: <c>sync_release_secrets</c> defaults to false, so no existing
    /// product starts pushing anything to GitHub and the manual token path every install uses today
    /// keeps working untouched. The three state columns stay null until the first successful push.
    /// </para>
    /// <para>
    /// <c>ix_products_ci_repo_id_sync_release_secrets</c> is unique on <c>ci_repo_id</c> and filtered on
    /// the flag: the Actions secret names (<c>WATCHTOWER_URL</c>, <c>WATCHTOWER_PRODUCT_ID</c>,
    /// <c>WATCHTOWER_RELEASE_TOKEN</c>) are fixed, so two products of one repository both syncing would
    /// overwrite each other's token on every reconcile pass. Filtered rather than plain unique because
    /// several products sharing one CI repo is otherwise entirely normal — the plain
    /// <c>ix_products_ci_repo_id</c> the foreign key needs is untouched, which is why this migration
    /// only creates and never drops. It cannot fail on existing data either: the new column is false
    /// everywhere, so the filter selects no rows.
    /// </para>
    /// </remarks>
    public partial class AddReleaseSecretSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "actions_synced_at",
                table: "products",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actions_synced_hash",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_actions_sync_error",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sync_release_secrets",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_products_ci_repo_id_sync_release_secrets",
                table: "products",
                column: "ci_repo_id",
                unique: true,
                filter: "\"sync_release_secrets\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_products_ci_repo_id_sync_release_secrets",
                table: "products");

            migrationBuilder.DropColumn(
                name: "actions_synced_at",
                table: "products");

            migrationBuilder.DropColumn(
                name: "actions_synced_hash",
                table: "products");

            migrationBuilder.DropColumn(
                name: "last_actions_sync_error",
                table: "products");

            migrationBuilder.DropColumn(
                name: "sync_release_secrets",
                table: "products");
        }
    }
}
