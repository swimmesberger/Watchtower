using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordOutdatedImageDigests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The remote digest behind each outdated image, so cached update flags can be revalidated
            // against the host's local images without asking a registry again. Existing rows back-fill
            // to "" (no recorded digests): they are skipped by revalidation and corrected by the next
            // full check, which is the same outcome as before this column existed.
            migrationBuilder.AddColumn<string>(
                name: "outdated_image_digests",
                table: "stack_update_checks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "outdated_image_digests",
                table: "stack_update_checks");
        }
    }
}
