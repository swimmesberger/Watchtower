using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "credentials",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    username = table.Column<string>(type: "TEXT", nullable: false),
                    token = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credentials", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "registries",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    credential_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registries", x => x.id);
                    table.ForeignKey(
                        name: "fk_registries_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "stacks",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    repository_url = table.Column<string>(type: "TEXT", nullable: false),
                    compose_file_path = table.Column<string>(type: "TEXT", nullable: false),
                    branch = table.Column<string>(type: "TEXT", nullable: false),
                    compose_project_name = table.Column<string>(type: "TEXT", nullable: false),
                    credential_id = table.Column<int>(type: "INTEGER", nullable: true),
                    webhook_token = table.Column<string>(type: "TEXT", nullable: true),
                    webhook_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_deployed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    last_deploy_status = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stacks", x => x.id);
                    table.ForeignKey(
                        name: "fk_stacks_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "deploy_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    stack_id = table.Column<int>(type: "INTEGER", nullable: false),
                    triggered_by = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    output = table.Column<string>(type: "TEXT", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deploy_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_deploy_events_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stack_env_vars",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    stack_id = table.Column<int>(type: "INTEGER", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stack_env_vars", x => x.id);
                    table.ForeignKey(
                        name: "fk_stack_env_vars_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stack_update_checks",
                columns: table => new
                {
                    stack_id = table.Column<int>(type: "INTEGER", nullable: false),
                    has_updates = table.Column<bool>(type: "INTEGER", nullable: false),
                    outdated_images = table.Column<string>(type: "TEXT", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stack_update_checks", x => x.stack_id);
                    table.ForeignKey(
                        name: "fk_stack_update_checks_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credentials_name",
                table: "credentials",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_deploy_events_stack_id_started_at",
                table: "deploy_events",
                columns: new[] { "stack_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_deploy_events_status",
                table: "deploy_events",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_registries_credential_id",
                table: "registries",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_registries_name",
                table: "registries",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_stack_env_vars_stack_id_key",
                table: "stack_env_vars",
                columns: new[] { "stack_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stacks_credential_id",
                table: "stacks",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_stacks_name",
                table: "stacks",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.DropTable(
                name: "deploy_events");

            migrationBuilder.DropTable(
                name: "registries");

            migrationBuilder.DropTable(
                name: "stack_env_vars");

            migrationBuilder.DropTable(
                name: "stack_update_checks");

            migrationBuilder.DropTable(
                name: "stacks");

            migrationBuilder.DropTable(
                name: "credentials");
        }
    }
}
