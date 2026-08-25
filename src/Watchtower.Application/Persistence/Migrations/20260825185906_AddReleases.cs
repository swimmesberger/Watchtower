using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <summary>
    /// ADR-0026 decision 3: <c>releases</c> and <c>release_images</c>, plus the per-product release
    /// webhook token on <c>products</c>.
    /// </summary>
    /// <remarks>
    /// Purely additive and inert on upgrade: no existing product has a release or a token, the webhook
    /// is disabled by default, and nothing about deploying reads either table in this stage — the
    /// back-compat contract (a product with no releases deploys exactly as before) is untouched by
    /// construction. The two unique indexes on <c>releases</c> are load-bearing rather than defensive:
    /// <c>(product_id, fingerprint)</c> is what makes two concurrent identical webhook calls produce one
    /// release, and <c>(product_id, version)</c> is what makes a reused version a refusal instead of an
    /// ambiguous label.
    /// </remarks>
    public partial class AddReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "release_webhook_enabled",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "release_webhook_token",
                table: "products",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "releases",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    product_id = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    branch = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    source_run_url = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_via = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_releases", x => x.id);
                    table.ForeignKey(
                        name: "fk_releases_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "release_images",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    release_id = table.Column<int>(type: "integer", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: true),
                    digest = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_release_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_release_images_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_products_release_webhook_token",
                table: "products",
                column: "release_webhook_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_release_images_release_id_repository",
                table: "release_images",
                columns: new[] { "release_id", "repository" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_releases_product_id_fingerprint",
                table: "releases",
                columns: new[] { "product_id", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_releases_product_id_id",
                table: "releases",
                columns: new[] { "product_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_releases_product_id_version",
                table: "releases",
                columns: new[] { "product_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "release_images");

            migrationBuilder.DropTable(
                name: "releases");

            migrationBuilder.DropIndex(
                name: "ix_products_release_webhook_token",
                table: "products");

            migrationBuilder.DropColumn(
                name: "release_webhook_enabled",
                table: "products");

            migrationBuilder.DropColumn(
                name: "release_webhook_token",
                table: "products");
        }
    }
}
