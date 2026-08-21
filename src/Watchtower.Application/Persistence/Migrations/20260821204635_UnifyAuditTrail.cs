using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnifyAuditTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The access-control trail folds into the one audit trail: every auth_events row becomes an
            // audit_events row in its plane's category (the kind is the action), with the actor and the
            // target resolved to names while the referenced rows still exist — the unified trail is
            // reference-free, so this is the last moment the names are joinable. Rejections record as
            // failures, which is what the Audit page tones. Oldest first, so the surrogate keys keep
            // arrival order (the trail pages by id).
            migrationBuilder.Sql("""
                INSERT INTO audit_events (category, action, target, detail, actor, success, error, created_at)
                SELECT
                    CASE
                        WHEN e.kind LIKE 'user.%' THEN 'users'
                        WHEN e.kind LIKE 'group.%' THEN 'groups'
                        WHEN e.kind LIKE 'realm.%' THEN 'realms'
                        WHEN e.kind LIKE 'access.%' OR e.kind LIKE 'route.%' THEN 'access'
                        ELSE 'auth'
                    END,
                    e.kind,
                    COALESCE(r.domain, u.user_name, ''),
                    e.detail,
                    u.user_name,
                    CASE WHEN e.kind IN ('login.failed', 'login.mfa.failed', 'access.denied') THEN 0 ELSE 1 END,
                    NULL,
                    e.created_at
                FROM auth_events e
                LEFT JOIN users u ON u.id = e.user_id
                LEFT JOIN routes r ON r.id = e.route_id
                ORDER BY e.id;
                """);

            migrationBuilder.DropTable(
                name: "auth_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    route_id = table.Column<int>(type: "INTEGER", nullable: true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    detail = table.Column<string>(type: "TEXT", nullable: true),
                    kind = table.Column<string>(type: "TEXT", nullable: false)
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
        }
    }
}
