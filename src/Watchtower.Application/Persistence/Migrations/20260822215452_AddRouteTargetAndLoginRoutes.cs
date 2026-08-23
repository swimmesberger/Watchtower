using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// ADR-0023, step one of two: the <em>shape</em>. Routes gain a <c>target</c>
    /// (<c>Service</c>/<c>Watchtower</c>), a nullable <c>stack_id</c> and a <c>realm_id</c>; realms gain
    /// a <c>login_route_id</c>. <c>realms.auth_host</c> is deliberately still here — the data conversion
    /// and the drop are <c>ConvertLoginHostsToRoutes</c>, the migration immediately after this one.
    /// <para>
    /// <b>Why two migrations.</b> The conversion has to insert Watchtower routes, which are exactly the
    /// rows with a null <c>stack_id</c> — and EF's SQLite generator hoists every table rebuild (which is
    /// how a column becomes nullable) to the <em>end</em> of the migration it appears in, while raw SQL
    /// keeps its position. So no ordering within one migration can make "stack_id is nullable" true at
    /// the moment the conversion runs. Splitting it is what makes both halves possible.
    /// </para>
    /// </remarks>
    public partial class AddRouteTargetAndLoginRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "stack_id",
                table: "routes",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "identity_header_mode",
                table: "routes",
                type: "TEXT",
                nullable: false,
                defaultValue: "None",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "access_mode",
                table: "routes",
                type: "TEXT",
                nullable: false,
                defaultValue: "Public",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "realm_id",
                table: "routes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target",
                table: "routes",
                type: "TEXT",
                nullable: false,
                defaultValue: "Service");

            migrationBuilder.AddColumn<int>(
                name: "login_route_id",
                table: "realms",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_routes_realm_id",
                table: "routes",
                column: "realm_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_routes_target",
                table: "routes",
                sql: "(\"target\" = 'Watchtower' AND \"stack_id\" IS NULL AND \"realm_id\" IS NOT NULL AND \"access_mode\" = 'Public')\nOR (\"target\" = 'Service' AND \"stack_id\" IS NOT NULL AND \"realm_id\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_realms_login_route_id",
                table: "realms",
                column: "login_route_id",
                unique: true,
                filter: "\"login_route_id\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_realms_routes_login_route_id",
                table: "realms",
                column: "login_route_id",
                principalTable: "routes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_routes_realms_realm_id",
                table: "routes",
                column: "realm_id",
                principalTable: "realms",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_realms_routes_login_route_id",
                table: "realms");

            migrationBuilder.DropForeignKey(
                name: "fk_routes_realms_realm_id",
                table: "routes");

            migrationBuilder.DropIndex(
                name: "ix_routes_realm_id",
                table: "routes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_routes_target",
                table: "routes");

            migrationBuilder.DropIndex(
                name: "ix_realms_login_route_id",
                table: "realms");

            migrationBuilder.DropColumn(
                name: "realm_id",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "target",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "login_route_id",
                table: "realms");

            migrationBuilder.AlterColumn<int>(
                name: "stack_id",
                table: "routes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "identity_header_mode",
                table: "routes",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "None");

            migrationBuilder.AlterColumn<string>(
                name: "access_mode",
                table: "routes",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "Public");
        }
    }
}
