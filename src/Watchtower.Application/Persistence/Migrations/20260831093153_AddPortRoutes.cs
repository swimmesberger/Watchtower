using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPortRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_routes_domain",
                table: "routes");

            migrationBuilder.AlterColumn<string>(
                name: "domain",
                table: "routes",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "binding",
                table: "routes",
                type: "text",
                nullable: false,
                defaultValue: "Domain");

            migrationBuilder.AddColumn<int>(
                name: "listen_port",
                table: "routes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_routes_domain",
                table: "routes",
                column: "domain",
                unique: true,
                filter: "\"domain\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_routes_listen_port",
                table: "routes",
                column: "listen_port",
                unique: true,
                filter: "\"listen_port\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_routes_binding",
                table: "routes",
                sql: "(\"binding\" = 'Domain' AND \"domain\" IS NOT NULL AND \"listen_port\" IS NULL)\nOR (\"binding\" = 'Port' AND \"domain\" IS NULL AND \"listen_port\" BETWEEN 1 AND 65535\n    AND \"target\" = 'Service' AND \"access_mode\" = 'Public' AND \"tls_enabled\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_routes_domain",
                table: "routes");

            migrationBuilder.DropIndex(
                name: "ix_routes_listen_port",
                table: "routes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_routes_binding",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "binding",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "listen_port",
                table: "routes");

            migrationBuilder.AlterColumn<string>(
                name: "domain",
                table: "routes",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_routes_domain",
                table: "routes",
                column: "domain",
                unique: true);
        }
    }
}
