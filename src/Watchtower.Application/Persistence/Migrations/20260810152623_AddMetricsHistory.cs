using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricsHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "metric_container_samples",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    tier_seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    t_unix_seconds = table.Column<long>(type: "INTEGER", nullable: false),
                    container_name = table.Column<string>(type: "TEXT", nullable: false),
                    stack_name = table.Column<string>(type: "TEXT", nullable: true),
                    cpu_percent = table.Column<double>(type: "REAL", nullable: false),
                    mem_used_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    mem_limit_bytes = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metric_container_samples", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metric_host_samples",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    tier_seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    t_unix_seconds = table.Column<long>(type: "INTEGER", nullable: false),
                    cpu_percent = table.Column<double>(type: "REAL", nullable: true),
                    mem_percent = table.Column<double>(type: "REAL", nullable: true),
                    mem_used_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    load_avg1 = table.Column<double>(type: "REAL", nullable: true),
                    load_avg5 = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metric_host_samples", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_metric_container_samples_tier_seconds_t_unix_seconds_container_name",
                table: "metric_container_samples",
                columns: new[] { "tier_seconds", "t_unix_seconds", "container_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_metric_host_samples_tier_seconds_t_unix_seconds",
                table: "metric_host_samples",
                columns: new[] { "tier_seconds", "t_unix_seconds" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metric_container_samples");

            migrationBuilder.DropTable(
                name: "metric_host_samples");
        }
    }
}
