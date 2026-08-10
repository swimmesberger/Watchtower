using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateManagementGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "template_management_grants",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    stack_id = table.Column<int>(type: "INTEGER", nullable: false),
                    template_id = table.Column<int>(type: "INTEGER", nullable: false),
                    allow_delete = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_management_grants", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_management_grants_stack_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "stack_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_template_management_grants_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_template_management_grants_stack_id_template_id",
                table: "template_management_grants",
                columns: new[] { "stack_id", "template_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_management_grants_template_id",
                table: "template_management_grants",
                column: "template_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "template_management_grants");
        }
    }
}
