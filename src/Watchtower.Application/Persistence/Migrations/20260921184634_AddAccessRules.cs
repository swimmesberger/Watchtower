using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    realm_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_access_rules_realms_realm_id",
                        column: x => x.realm_id,
                        principalTable: "realms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "access_rule_clauses",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    access_rule_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    group_id = table.Column<int>(type: "integer", nullable: true),
                    value = table.Column<string>(type: "text", nullable: true),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_rule_clauses", x => x.id);
                    table.CheckConstraint("ck_access_rule_clauses_subject", "(\"kind\" = 'User' AND \"user_id\" IS NOT NULL AND \"group_id\" IS NULL AND \"value\" IS NULL) OR (\"kind\" = 'Group' AND \"group_id\" IS NOT NULL AND \"user_id\" IS NULL AND \"value\" IS NULL) OR (\"kind\" IN ('Email', 'EmailDomain', 'ExternalGroup', 'ExternalPolicy') AND \"value\" IS NOT NULL AND \"user_id\" IS NULL AND \"group_id\" IS NULL)");
                    table.ForeignKey(
                        name: "fk_access_rule_clauses_access_rules_access_rule_id",
                        column: x => x.access_rule_id,
                        principalTable: "access_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_access_rule_clauses_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_access_rule_clauses_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "route_access_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    route_id = table.Column<int>(type: "integer", nullable: false),
                    access_rule_id = table.Column<int>(type: "integer", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_route_access_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_route_access_rules_access_rules_access_rule_id",
                        column: x => x.access_rule_id,
                        principalTable: "access_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_route_access_rules_routes_route_id",
                        column: x => x.route_id,
                        principalTable: "routes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_access_rule_clauses_access_rule_id_group_id",
                table: "access_rule_clauses",
                columns: new[] { "access_rule_id", "group_id" },
                unique: true,
                filter: "\"group_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_access_rule_clauses_access_rule_id_kind_value",
                table: "access_rule_clauses",
                columns: new[] { "access_rule_id", "kind", "value" },
                unique: true,
                filter: "\"value\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_access_rule_clauses_access_rule_id_order",
                table: "access_rule_clauses",
                columns: new[] { "access_rule_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_access_rule_clauses_access_rule_id_user_id",
                table: "access_rule_clauses",
                columns: new[] { "access_rule_id", "user_id" },
                unique: true,
                filter: "\"user_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_access_rule_clauses_group_id",
                table: "access_rule_clauses",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_access_rule_clauses_user_id",
                table: "access_rule_clauses",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_access_rules_realm_id_normalized_name",
                table: "access_rules",
                columns: new[] { "realm_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_route_access_rules_access_rule_id",
                table: "route_access_rules",
                column: "access_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_route_access_rules_route_id_access_rule_id",
                table: "route_access_rules",
                columns: new[] { "route_id", "access_rule_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_route_access_rules_route_id_order",
                table: "route_access_rules",
                columns: new[] { "route_id", "order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_rule_clauses");

            migrationBuilder.DropTable(
                name: "route_access_rules");

            migrationBuilder.DropTable(
                name: "access_rules");
        }
    }
}
