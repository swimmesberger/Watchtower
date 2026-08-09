using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing routes keep today's behaviour: "Public" (the enum name, not the scaffolded "")
            // is the only value the string converter can read back.
            migrationBuilder.AddColumn<string>(
                name: "access_mode",
                table: "routes",
                type: "TEXT",
                nullable: false,
                defaultValue: "Public");

            migrationBuilder.AddColumn<string>(
                name: "bypass_paths",
                table: "routes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_name = table.Column<string>(type: "TEXT", nullable: false),
                    normalized_user_name = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: true),
                    password_hash = table.Column<string>(type: "TEXT", nullable: false),
                    is_admin = table.Column<bool>(type: "INTEGER", nullable: false),
                    disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    access_failed_count = table.Column<int>(type: "INTEGER", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    security_stamp = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "auth_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<int>(type: "INTEGER", nullable: true),
                    route_id = table.Column<int>(type: "INTEGER", nullable: true),
                    detail = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_events_routes_route_id",
                        column: x => x.route_id,
                        principalTable: "routes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_auth_events_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "auth_sessions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    route_id = table.Column<int>(type: "INTEGER", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_sessions_routes_route_id",
                        column: x => x.route_id,
                        principalTable: "routes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_auth_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "login_codes",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    code_hash = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false),
                    route_id = table.Column<int>(type: "INTEGER", nullable: false),
                    redirect_uri = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_login_codes_routes_route_id",
                        column: x => x.route_id,
                        principalTable: "routes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_login_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "route_access_grants",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    route_id = table.Column<int>(type: "INTEGER", nullable: false),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_route_access_grants", x => x.id);
                    table.ForeignKey(
                        name: "fk_route_access_grants_routes_route_id",
                        column: x => x.route_id,
                        principalTable: "routes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_route_access_grants_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_events_created_at",
                table: "auth_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_events_route_id",
                table: "auth_events",
                column: "route_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_events_user_id",
                table: "auth_events",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_route_id",
                table: "auth_sessions",
                column: "route_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_token_hash",
                table: "auth_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_user_id",
                table: "auth_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_code_hash",
                table: "login_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_expires_at",
                table: "login_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_route_id",
                table: "login_codes",
                column: "route_id");

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_user_id",
                table: "login_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_route_access_grants_route_id_user_id",
                table: "route_access_grants",
                columns: new[] { "route_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_route_access_grants_user_id",
                table: "route_access_grants",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_user_name",
                table: "users",
                column: "normalized_user_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_events");

            migrationBuilder.DropTable(
                name: "auth_sessions");

            migrationBuilder.DropTable(
                name: "login_codes");

            migrationBuilder.DropTable(
                name: "route_access_grants");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropColumn(
                name: "access_mode",
                table: "routes");

            migrationBuilder.DropColumn(
                name: "bypass_paths",
                table: "routes");
        }
    }
}
