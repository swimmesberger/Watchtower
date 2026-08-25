using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <summary>
    /// ADR-0026: the git source moves off <c>stacks</c> and <c>stack_templates</c> onto a new
    /// <c>products</c> table, and both tables gain a required <c>product_id</c> plus a nullable
    /// <c>branch_override</c>.
    /// </summary>
    /// <remarks>
    /// The order is what makes this safe on a populated database: create the table and the nullable
    /// columns, <em>then</em> backfill from the values still in place, <em>then</em> drop the old
    /// columns and tighten <c>product_id</c>. EF wraps the whole migration in one transaction, so a
    /// failure anywhere leaves the estate exactly as it was.
    /// <para>
    /// The backfill itself is <see cref="ProductBackfillSql"/>, kept in its own file so its grouping
    /// rule can be read and reviewed against <c>Services.ProductSourceKey</c> — the C# the same rule is
    /// spelled in, which <c>stacks.create</c> find-or-creates with.
    /// </para>
    /// </remarks>
    public partial class AddProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    repository_url = table.Column<string>(type: "text", nullable: false),
                    compose_file_path = table.Column<string>(type: "text", nullable: false),
                    default_branch = table.Column<string>(type: "text", nullable: false),
                    credential_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.ForeignKey(
                        name: "fk_products_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_products_credential_id",
                table: "products",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_name",
                table: "products",
                column: "name",
                unique: true);

            // Nullable for now: the rows that will fill them are still holding their own source columns.
            migrationBuilder.AddColumn<int>(
                name: "product_id",
                table: "stacks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "branch_override",
                table: "stacks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "product_id",
                table: "stack_templates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "branch_override",
                table: "stack_templates",
                type: "text",
                nullable: true);

            // One product per normalized (repository URL, compose path) across stacks ∪ stack_templates;
            // a branch that differs from the product's default becomes an override. A template's tenants
            // carry identical copied fields today, so they collapse onto one product — which is the
            // propagation fix, landing for free.
            migrationBuilder.Sql(ProductBackfillSql.Sql);

            migrationBuilder.DropForeignKey(
                name: "fk_stacks_credentials_credential_id",
                table: "stacks");

            migrationBuilder.DropIndex(
                name: "ix_stacks_credential_id",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "repository_url",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "compose_file_path",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "branch",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "credential_id",
                table: "stacks");

            migrationBuilder.DropForeignKey(
                name: "fk_stack_templates_credentials_credential_id",
                table: "stack_templates");

            migrationBuilder.DropIndex(
                name: "ix_stack_templates_credential_id",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "repository_url",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "compose_file_path",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "branch",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "credential_id",
                table: "stack_templates");

            // Every row has a product now, so the column can carry the model's required shape.
            migrationBuilder.AlterColumn<int>(
                name: "product_id",
                table: "stacks",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "product_id",
                table: "stack_templates",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_stacks_product_id",
                table: "stacks",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stack_templates_product_id",
                table: "stack_templates",
                column: "product_id");

            migrationBuilder.AddForeignKey(
                name: "fk_stacks_products_product_id",
                table: "stacks",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stack_templates_products_product_id",
                table: "stack_templates",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <summary>
        /// Best-effort reversal: the four columns come back, filled from the product each row points at
        /// plus its own branch override. What cannot come back is the <em>distinction</em> the product
        /// introduced — two stacks that were merged onto one product are given identical source columns
        /// again, which is what they had before the merge anyway.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_stacks_products_product_id",
                table: "stacks");

            migrationBuilder.DropForeignKey(
                name: "fk_stack_templates_products_product_id",
                table: "stack_templates");

            migrationBuilder.DropIndex(
                name: "ix_stacks_product_id",
                table: "stacks");

            migrationBuilder.DropIndex(
                name: "ix_stack_templates_product_id",
                table: "stack_templates");

            migrationBuilder.AddColumn<string>(
                name: "repository_url",
                table: "stacks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "compose_file_path",
                table: "stacks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "branch",
                table: "stacks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "credential_id",
                table: "stacks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_url",
                table: "stack_templates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "compose_file_path",
                table: "stack_templates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "branch",
                table: "stack_templates",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "credential_id",
                table: "stack_templates",
                type: "integer",
                nullable: true);

            // The effective branch is the same two-level coalesce ProductSourceResolver applies: the
            // stack's own override, else its template's, else the product default. Two statements
            // because PostgreSQL will not let an UPDATE's outer join reference the target table.
            migrationBuilder.Sql("""
                UPDATE stacks s
                   SET repository_url = p.repository_url,
                       compose_file_path = p.compose_file_path,
                       branch = coalesce(s.branch_override, p.default_branch),
                       credential_id = p.credential_id
                  FROM products p
                 WHERE p.id = s.product_id;
                """);

            migrationBuilder.Sql("""
                UPDATE stacks s
                   SET branch = t.branch_override
                  FROM stack_templates t
                 WHERE t.id = s.template_id
                   AND s.branch_override IS NULL
                   AND t.branch_override IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE stack_templates t
                   SET repository_url = p.repository_url,
                       compose_file_path = p.compose_file_path,
                       branch = coalesce(t.branch_override, p.default_branch),
                       credential_id = p.credential_id
                  FROM products p
                 WHERE p.id = t.product_id;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_stacks_credential_id",
                table: "stacks",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_stack_templates_credential_id",
                table: "stack_templates",
                column: "credential_id");

            migrationBuilder.AddForeignKey(
                name: "fk_stacks_credentials_credential_id",
                table: "stacks",
                column: "credential_id",
                principalTable: "credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_stack_templates_credentials_credential_id",
                table: "stack_templates",
                column: "credential_id",
                principalTable: "credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "branch_override",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "branch_override",
                table: "stack_templates");

            migrationBuilder.DropTable(
                name: "products");
        }
    }
}
