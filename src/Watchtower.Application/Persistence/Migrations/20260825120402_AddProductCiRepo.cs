using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <summary>
    /// ADR-0026 decision 7: <c>products.ci_repo_id</c> replaces matching a stack's repository URL
    /// against <c>ci_repos</c> on every read.
    /// </summary>
    /// <remarks>
    /// Purely additive, and deliberately left null for every existing row: parsing repository URLs in
    /// SQL is not worth it when the first CI read of a product does the same work in C# and records the
    /// answer. <c>ON DELETE SET NULL</c> — removing a repository from CI must not take the products
    /// deploying it, and the column is not unique because several products can share one CI repo.
    /// </remarks>
    public partial class AddProductCiRepo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ci_repo_id",
                table: "products",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_ci_repo_id",
                table: "products",
                column: "ci_repo_id");

            migrationBuilder.AddForeignKey(
                name: "fk_products_ci_repos_ci_repo_id",
                table: "products",
                column: "ci_repo_id",
                principalTable: "ci_repos",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_products_ci_repos_ci_repo_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_products_ci_repo_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "ci_repo_id",
                table: "products");
        }
    }
}
