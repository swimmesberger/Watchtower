using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupServiceOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stack_backup_service_overrides",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    stack_id = table.Column<int>(type: "INTEGER", nullable: false),
                    service = table.Column<string>(type: "TEXT", nullable: false),
                    exclude = table.Column<bool>(type: "INTEGER", nullable: false),
                    stop = table.Column<string>(type: "TEXT", nullable: true),
                    dump = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stack_backup_service_overrides", x => x.id);
                    table.ForeignKey(
                        name: "fk_stack_backup_service_overrides_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stack_backup_service_overrides_stack_id_service",
                table: "stack_backup_service_overrides",
                columns: new[] { "stack_id", "service" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stack_backup_service_overrides");
        }
    }
}
