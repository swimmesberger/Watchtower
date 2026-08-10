using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRealms : Migration
    {
        /// <summary>
        /// Timestamp stamped on the seeded system realm. A literal, not <c>DateTimeOffset.UtcNow</c>: a
        /// migration must produce the same rows on every instance that applies it, and "when this
        /// deployment happened to upgrade" is not a fact about the operator population.
        /// </summary>
        private static readonly DateTimeOffset SystemRealmCreatedAt =
            new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order is load-bearing and hand-arranged (the scaffolded order put the columns first): the
            // realms table and its one seeded row have to exist before anything can point a foreign key at
            // realm 1, because adding a foreign key on SQLite rebuilds the whole table and the rebuild's
            // INSERT…SELECT is checked against it.
            migrationBuilder.CreateTable(
                name: "realms",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    slug = table.Column<string>(type: "TEXT", nullable: false),
                    auth_host = table.Column<string>(type: "TEXT", nullable: true),
                    is_system = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_realms", x => x.id);
                });

            // The system realm, with the explicit id every realm column below defaults to. Its auth_host is
            // null on purpose and stays null: the operator login host is the configured Watchtower:Auth:Host,
            // so authentication never has to read a row to find its own login page (design.md §13).
            migrationBuilder.InsertData(
                table: "realms",
                columns: new[] { "id", "name", "slug", "auth_host", "is_system", "created_at" },
                values: new object[] { 1, "Operator", "operator", null, true, SystemRealmCreatedAt });

            migrationBuilder.DropIndex(
                name: "ix_users_normalized_user_name",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_groups_normalized_name",
                table: "groups");

            // defaultValue 1, not the scaffolded 0: this is the backfill. Every row that existed before
            // realms did belongs to the operator population, so an upgraded instance keeps behaving exactly
            // as it did — and 0 would be an id no realm has, i.e. an immediately violated foreign key.
            migrationBuilder.AddColumn<int>(
                name: "realm_id",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "realm_id",
                table: "stack_templates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "realm_id",
                table: "groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ix_users_realm_id_normalized_user_name",
                table: "users",
                columns: new[] { "realm_id", "normalized_user_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stack_templates_realm_id",
                table: "stack_templates",
                column: "realm_id");

            migrationBuilder.CreateIndex(
                name: "ix_groups_realm_id_normalized_name",
                table: "groups",
                columns: new[] { "realm_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_realms_auth_host",
                table: "realms",
                column: "auth_host",
                unique: true,
                filter: "\"auth_host\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_realms_slug",
                table: "realms",
                column: "slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_groups_realms_realm_id",
                table: "groups",
                column: "realm_id",
                principalTable: "realms",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_stack_templates_realms_realm_id",
                table: "stack_templates",
                column: "realm_id",
                principalTable: "realms",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_users_realms_realm_id",
                table: "users",
                column: "realm_id",
                principalTable: "realms",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Hand-written, and the only edited lines in this method: the v1 schema enforces one *global*
            // credential space, so it simply cannot represent a second population — two realms each holding
            // an `admin` would make the unique index recreated at the bottom fail outright. Dropping the
            // non-operator accounts and groups (their sessions, memberships and route grants follow through
            // the existing cascades) is the only rollback the old shape can express, and it is the
            // semantically correct one: v1 knew about the operator realm and nothing else. Categories are
            // left alone — stack_templates.name is globally unique either way, so losing only the column is
            // lossless there.
            migrationBuilder.Sql("DELETE FROM users WHERE realm_id <> 1;");
            migrationBuilder.Sql("DELETE FROM groups WHERE realm_id <> 1;");

            // Hand-written, and the second edit in this method: the scaffolded DropTable("realms") does not
            // execute. SQLite drops a foreign key by rebuilding the table, and EF's SQLite generator hoists
            // every rebuilding operation to the *end* of the migration — so wherever DropTable sits in this
            // method, it is emitted while users, groups and stack_templates still carry realm_id with a
            // RESTRICT foreign key, and every row still points at realm 1. DROP TABLE runs an implicit
            // delete of those rows, which the constraint refuses: 'FOREIGN KEY constraint failed', and the
            // migration is irreversible. Reordering the C# does not help, because the hoist happens after
            // this method has run.
            //
            // So the drop states its own conditions instead. suppressTransaction is what makes it work at
            // all: PRAGMA foreign_keys is a no-op inside a transaction, and EF wraps a migration in one.
            // Enforcement is disabled for this one statement only — deliberately *after* the two DELETEs
            // above, which need it on so their cascades still take the deleted accounts' sessions,
            // memberships and route grants with them. The dangling reference the three tables are left
            // holding until the hoisted rebuild drops the column is never enforced: the rebuild only reads
            // them, and the tables it writes have no such constraint.
            migrationBuilder.Sql(
                """
                PRAGMA foreign_keys = OFF;
                DROP TABLE "realms";
                PRAGMA foreign_keys = ON;
                """,
                suppressTransaction: true);

            migrationBuilder.DropForeignKey(
                name: "fk_groups_realms_realm_id",
                table: "groups");

            migrationBuilder.DropForeignKey(
                name: "fk_stack_templates_realms_realm_id",
                table: "stack_templates");

            migrationBuilder.DropForeignKey(
                name: "fk_users_realms_realm_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_realm_id_normalized_user_name",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_stack_templates_realm_id",
                table: "stack_templates");

            migrationBuilder.DropIndex(
                name: "ix_groups_realm_id_normalized_name",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "realm_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "realm_id",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "realm_id",
                table: "groups");

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_user_name",
                table: "users",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_groups_normalized_name",
                table: "groups",
                column: "normalized_name",
                unique: true);
        }
    }
}
