using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <summary>
    /// Stage 7 of ADR-0026: the persisted <c>stacks.backup_directory</c>, the three stack backup fields
    /// turned tri-state, the template-level backup policy every tenant inherits, and the template-level
    /// per-service overrides table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Additive and behaviour-preserving.</b> The three <c>stacks</c> columns only change type —
    /// <c>NOT NULL</c> to nullable — so every existing row keeps the value it had, <em>as an explicit
    /// value</em>. That is the point: a stack an operator configured must not start inheriting because
    /// inheritance became possible. Only rows written after this migration start out null, and
    /// <c>TenantProvisioningService</c> is the writer that deliberately does so.
    /// </para>
    /// <para>
    /// <b><c>backup_directory</c> is backfilled with nothing, deliberately.</b> Its value is
    /// <c>{instance}/{stack}</c>, and the instance name is <em>configuration</em>
    /// (<c>Backup:InstanceName</c>, defaulting to the machine name) — SQL cannot see it, so any value
    /// this migration invented would be a guess that silently orphaned every archive an install already
    /// holds. Null therefore means "compute it exactly as we always did"
    /// (<c>BackupNaming.ResolveDirectory</c>), which is byte-for-byte the pre-stage-7 behaviour, and a
    /// legacy stack is stamped with that computed value after its next <em>successful</em> backup — the
    /// moment the value is known to be where the bytes really went. New stacks and new tenants are
    /// stamped at creation, so the rename hazard is closed for everything created from here on and for
    /// everything that gets backed up once.
    /// </para>
    /// <para>
    /// The column default on <c>stacks.backup_quiesce_mode</c> is dropped rather than kept: a default
    /// would make a row written without an opinion say <c>Stop</c> explicitly, which is exactly the
    /// state the tri-state exists to distinguish from silence. The instance default moved into
    /// <c>BackupPolicyResolver</c>, where the other two rungs of the ladder live.
    /// </para>
    /// <para>
    /// One table and one index created, none dropped (the stage-5 trap does not apply — nothing else
    /// indexes these columns). The <c>Down</c> fills the nulls it is about to forbid with the same
    /// defaults the resolver applies, so a rollback reproduces today's behaviour rather than failing on
    /// the <c>NOT NULL</c> or turning "inherit" into <c>false</c>.
    /// </para>
    /// </remarks>
    public partial class AddTenantAwareBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "backup_stop_containers",
                table: "stacks",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<string>(
                name: "backup_quiesce_mode",
                table: "stacks",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "Stop");

            migrationBuilder.AlterColumn<bool>(
                name: "backup_enabled",
                table: "stacks",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AddColumn<string>(
                name: "backup_directory",
                table: "stacks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "backup_cron",
                table: "stack_templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "backup_enabled",
                table: "stack_templates",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "backup_quiesce_mode",
                table: "stack_templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "backup_stop_containers",
                table: "stack_templates",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "template_backup_service_overrides",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    template_id = table.Column<int>(type: "integer", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    exclude = table.Column<bool>(type: "boolean", nullable: false),
                    stop = table.Column<string>(type: "text", nullable: true),
                    dump = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_backup_service_overrides", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_backup_service_overrides_stack_templates_template_",
                        column: x => x.template_id,
                        principalTable: "stack_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_template_backup_service_overrides_template_id_service",
                table: "template_backup_service_overrides",
                columns: new[] { "template_id", "service" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "template_backup_service_overrides");

            migrationBuilder.DropColumn(
                name: "backup_directory",
                table: "stacks");

            migrationBuilder.DropColumn(
                name: "backup_cron",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "backup_enabled",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "backup_quiesce_mode",
                table: "stack_templates");

            migrationBuilder.DropColumn(
                name: "backup_stop_containers",
                table: "stack_templates");

            // The nulls have to become the values the resolver would have produced for them, or the
            // NOT NULL below fails outright — and "inherit" would silently become false, which is the
            // opposite of the instance default for this one.
            migrationBuilder.Sql(
                "UPDATE stacks SET backup_stop_containers = TRUE WHERE backup_stop_containers IS NULL;");
            migrationBuilder.AlterColumn<bool>(
                name: "backup_stop_containers",
                table: "stacks",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.Sql(
                "UPDATE stacks SET backup_quiesce_mode = 'Stop' WHERE backup_quiesce_mode IS NULL;");
            migrationBuilder.AlterColumn<string>(
                name: "backup_quiesce_mode",
                table: "stacks",
                type: "text",
                nullable: false,
                defaultValue: "Stop",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.Sql(
                "UPDATE stacks SET backup_enabled = FALSE WHERE backup_enabled IS NULL;");
            migrationBuilder.AlterColumn<bool>(
                name: "backup_enabled",
                table: "stacks",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);
        }
    }
}
