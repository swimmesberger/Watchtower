using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "routes",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    stack_id = table.Column<int>(type: "INTEGER", nullable: false),
                    domain = table.Column<string>(type: "TEXT", nullable: false),
                    service_name = table.Column<string>(type: "TEXT", nullable: false),
                    container_port = table.Column<int>(type: "INTEGER", nullable: false),
                    tls_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_primary = table.Column<bool>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    status_detail = table.Column<string>(type: "TEXT", nullable: true),
                    cert_not_after = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_routes", x => x.id);
                    table.ForeignKey(
                        name: "fk_routes_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_routes_domain",
                table: "routes",
                column: "domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_routes_stack_id",
                table: "routes",
                column: "stack_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "routes");
        }
    }
}
