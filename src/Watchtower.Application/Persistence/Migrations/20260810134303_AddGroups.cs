using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_route_access_grants_route_id_user_id",
                table: "route_access_grants");

            migrationBuilder.AlterColumn<int>(
                name: "user_id",
                table: "route_access_grants",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "group_id",
                table: "route_access_grants",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    normalized_name = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "group_members",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    group_id = table.Column<int>(type: "INTEGER", nullable: false),
                    user_id = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_group_members_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_route_access_grants_group_id",
                table: "route_access_grants",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_route_access_grants_route_id_group_id",
                table: "route_access_grants",
                columns: new[] { "route_id", "group_id" },
                unique: true,
                filter: "\"group_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_route_access_grants_route_id_user_id",
                table: "route_access_grants",
                columns: new[] { "route_id", "user_id" },
                unique: true,
                filter: "\"user_id\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_route_access_grants_subject",
                table: "route_access_grants",
                sql: "(\"user_id\" IS NOT NULL) <> (\"group_id\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_group_members_group_id_user_id",
                table: "group_members",
                columns: new[] { "group_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_group_members_user_id",
                table: "group_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_groups_normalized_name",
                table: "groups",
                column: "normalized_name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_route_access_grants_groups_group_id",
                table: "route_access_grants",
                column: "group_id",
                principalTable: "groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Hand-written, and the only edited line in this file: everything below is as scaffolded.
            //
            // The v1 schema has no group_id column and requires user_id to be NOT NULL, so it simply cannot
            // represent a group grant — a downgrade has to drop them, and dropping them is the semantically
            // correct rollback (the route reverts to exactly the direct grants v1 knew about). Without this
            // the scaffolded rebuild below is actively wrong in two ways: its `IFNULL(user_id, 0)` would
            // rewrite every group grant into a grant for user 0, and the non-partial unique index it
            // recreates on (route_id, user_id) would then fail outright for any route holding two of them.
            migrationBuilder.Sql("DELETE FROM route_access_grants WHERE group_id IS NOT NULL;");

            migrationBuilder.DropForeignKey(
                name: "fk_route_access_grants_groups_group_id",
                table: "route_access_grants");

            migrationBuilder.DropTable(
                name: "group_members");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropIndex(
                name: "ix_route_access_grants_group_id",
                table: "route_access_grants");

            migrationBuilder.DropIndex(
                name: "ix_route_access_grants_route_id_group_id",
                table: "route_access_grants");

            migrationBuilder.DropIndex(
                name: "ix_route_access_grants_route_id_user_id",
                table: "route_access_grants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_route_access_grants_subject",
                table: "route_access_grants");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "route_access_grants");

            migrationBuilder.AlterColumn<int>(
                name: "user_id",
                table: "route_access_grants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_route_access_grants_route_id_user_id",
                table: "route_access_grants",
                columns: new[] { "route_id", "user_id" },
                unique: true);
        }
    }
}
