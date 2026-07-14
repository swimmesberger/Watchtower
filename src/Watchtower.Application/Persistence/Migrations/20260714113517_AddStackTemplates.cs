using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStackTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "template_id",
                table: "stacks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tenant_slug",
                table: "stacks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "stack_templates",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    repository_url = table.Column<string>(type: "TEXT", nullable: false),
                    compose_file_path = table.Column<string>(type: "TEXT", nullable: false),
                    branch = table.Column<string>(type: "TEXT", nullable: false),
                    credential_id = table.Column<int>(type: "INTEGER", nullable: true),
                    domain_pattern = table.Column<string>(type: "TEXT", nullable: false),
                    target_service_name = table.Column<string>(type: "TEXT", nullable: false),
                    target_port = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stack_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_stack_templates_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "stack_template_env_vars",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    template_id = table.Column<int>(type: "INTEGER", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stack_template_env_vars", x => x.id);
                    table.ForeignKey(
                        name: "fk_stack_template_env_vars_stack_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "stack_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stacks_template_id_tenant_slug",
                table: "stacks",
                columns: new[] { "template_id", "tenant_slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stack_template_env_vars_template_id_key",
                table: "stack_template_env_vars",
                columns: new[] { "template_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stack_templates_credential_id",
                table: "stack_templates",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_stack_templates_name",
                table: "stack_templates",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_stacks_stack_templates_template_id",
                table: "stacks",
                column: "template_id",
                principalTable: "stack_templates",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_stacks_stack_templates_template_id",
                table: "stacks");

            migrationBuilder.DropTable(
                name: "stack_template_env_vars");

            migrationBuilder.DropTable(
                name: "stack_templates");

            migrationBuilder.DropIndex(
                name: "ix_stacks_template_id_tenant_slug",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "template_id",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "tenant_slug",
                table: "stacks");
        }
    }
}
