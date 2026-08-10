using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteIdentityHeaderMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing routes forward the JWT only: "None" (the enum name, not the scaffolded "") is the only
            // value the string converter can read back — mirrors the access_mode "Public" back-fill.
            migrationBuilder.AddColumn<string>(
                name: "identity_header_mode",
                table: "routes",
                type: "TEXT",
                nullable: false,
                defaultValue: "None");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "identity_header_mode",
                table: "routes");
        }
    }
}
