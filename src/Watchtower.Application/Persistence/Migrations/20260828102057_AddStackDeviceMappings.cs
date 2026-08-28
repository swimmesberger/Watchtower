using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStackDeviceMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stack_device_mappings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    stack_id = table.Column<int>(type: "integer", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    host_path = table.Column<string>(type: "text", nullable: false),
                    container_path = table.Column<string>(type: "text", nullable: false),
                    permissions = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stack_device_mappings", x => x.id);
                    table.ForeignKey(
                        name: "fk_stack_device_mappings_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stack_device_mappings_stack_id_service_host_path",
                table: "stack_device_mappings",
                columns: new[] { "stack_id", "service", "host_path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stack_device_mappings");
        }
    }
}
