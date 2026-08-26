using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <summary>
    /// Stage 6 of ADR-0026: <c>stack_templates.default_pinned_release_id</c> (the fleet default
    /// <c>templates.setTenantsRelease</c> writes and provisioning copies onto each new tenant) and
    /// <c>products.retain_releases</c> (the release-retention floor the post-create pruning pass reads).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Additive and inert on upgrade. <c>default_pinned_release_id</c> is null everywhere, so every
    /// tenant provisioned after this migration tracks latest exactly as it did before; the foreign key
    /// is <c>SET NULL</c> because clearing a default for *future* tenants changes nothing that is
    /// running (unlike <c>stacks.pinned_release_id</c>, which is <c>Restrict</c>). What must not clear it
    /// silently is retention, and that is a rule in <c>ReleasePruner</c> rather than a schema one.
    /// </para>
    /// <para>
    /// <c>retain_releases</c> defaults to 50 for every existing product, and the pruning pass runs only
    /// when a release is accepted — so an install that never publishes another release deletes nothing,
    /// and one that does keeps everything pinned, defaulted, deployed or named by deploy history however
    /// far past 50 it sits.
    /// </para>
    /// <para>
    /// It creates one index and drops none: <c>ix_stack_templates_default_pinned_release_id</c> is the
    /// convention index for the new foreign key, which the pruner's template-default protection query
    /// also reads. (The stage-5 trap — a second index declaration over an already-indexed column
    /// suppressing the convention one and producing a silent <c>DropIndex</c> — does not apply here:
    /// nothing else indexes this column.)
    /// </para>
    /// </remarks>
    public partial class AddTenantReleasePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "default_pinned_release_id",
                table: "stack_templates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "retain_releases",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.CreateIndex(
                name: "ix_stack_templates_default_pinned_release_id",
                table: "stack_templates",
                column: "default_pinned_release_id");

            migrationBuilder.AddForeignKey(
                name: "fk_stack_templates_releases_default_pinned_release_id",
                table: "stack_templates",
                column: "default_pinned_release_id",
                principalTable: "releases",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_stack_templates_releases_default_pinned_release_id",
                table: "stack_templates");

            migrationBuilder.DropIndex(
                name: "ix_stack_templates_default_pinned_release_id",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "default_pinned_release_id",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "retain_releases",
                table: "products");
        }
    }
}
