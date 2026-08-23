using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    category = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: true),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "backup_paused_containers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    container_id = table.Column<string>(type: "text", nullable: false),
                    container_name = table.Column<string>(type: "text", nullable: false),
                    stack_name = table.Column<string>(type: "text", nullable: false),
                    paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_paused_containers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "credentials",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    token = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credentials", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "elarion_settings",
                columns: table => new
                {
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    value = table.Column<string>(type: "text", nullable: true),
                    updated_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_elarion_settings", x => new { x.kind, x.owner, x.key });
                });

            migrationBuilder.CreateTable(
                name: "metric_container_samples",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tier_seconds = table.Column<int>(type: "integer", nullable: false),
                    t_unix_seconds = table.Column<long>(type: "bigint", nullable: false),
                    container_name = table.Column<string>(type: "text", nullable: false),
                    stack_name = table.Column<string>(type: "text", nullable: true),
                    cpu_percent = table.Column<double>(type: "double precision", nullable: false),
                    mem_used_bytes = table.Column<long>(type: "bigint", nullable: false),
                    mem_limit_bytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metric_container_samples", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metric_host_samples",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tier_seconds = table.Column<int>(type: "integer", nullable: false),
                    t_unix_seconds = table.Column<long>(type: "bigint", nullable: false),
                    cpu_percent = table.Column<double>(type: "double precision", nullable: true),
                    mem_percent = table.Column<double>(type: "double precision", nullable: true),
                    mem_used_bytes = table.Column<long>(type: "bigint", nullable: true),
                    load_avg1 = table.Column<double>(type: "double precision", nullable: true),
                    load_avg5 = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metric_host_samples", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ci_repos",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    credential_id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    max_concurrent_runners = table.Column<int>(type: "integer", nullable: false),
                    runner_image = table.Column<string>(type: "text", nullable: true),
                    extra_labels = table.Column<string>(type: "text", nullable: true),
                    allow_docker_socket = table.Column<bool>(type: "boolean", nullable: false),
                    toolchain_profile_json = table.Column<string>(type: "text", nullable: true),
                    toolchain_detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    warmed_profile_hash = table.Column<string>(type: "text", nullable: true),
                    last_warmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_warm_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ci_repos", x => x.id);
                    table.ForeignKey(
                        name: "fk_ci_repos_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "registries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    credential_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                name: "auth_sessions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    route_id = table.Column<int>(type: "integer", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "backup_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    stack_id = table.Column<int>(type: "integer", nullable: false),
                    triggered_by = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    remote_path = table.Column<string>(type: "text", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    output = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deploy_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    stack_id = table.Column<int>(type: "integer", nullable: false),
                    triggered_by = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    output = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deploy_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "group_members",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_members", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    realm_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "login_codes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    route_id = table.Column<int>(type: "integer", nullable: false),
                    redirect_uri = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_codes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "realms",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    login_route_id = table.Column<int>(type: "integer", nullable: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_realms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stack_templates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    realm_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    repository_url = table.Column<string>(type: "text", nullable: false),
                    compose_file_path = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    credential_id = table.Column<int>(type: "integer", nullable: true),
                    domain_pattern = table.Column<string>(type: "text", nullable: false),
                    target_service_name = table.Column<string>(type: "text", nullable: false),
                    target_port = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stack_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_stack_templates_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_stack_templates_realms_realm_id",
                        column: x => x.realm_id,
                        principalTable: "realms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    realm_id = table.Column<int>(type: "integer", nullable: false),
                    user_name = table.Column<string>(type: "text", nullable: false),
                    normalized_user_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    is_admin = table.Column<bool>(type: "boolean", nullable: false),
                    disabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    authenticator_key = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_realms_realm_id",
                        column: x => x.realm_id,
                        principalTable: "realms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stack_template_env_vars",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    template_id = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stack_template_env_vars", x => x.id);
                    table.ForeignKey(
                        name: "fk_stack_template_env_vars_stack_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "stack_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stacks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    repository_url = table.Column<string>(type: "text", nullable: false),
                    compose_file_path = table.Column<string>(type: "text", nullable: false),
                    branch = table.Column<string>(type: "text", nullable: false),
                    compose_project_name = table.Column<string>(type: "text", nullable: false),
                    credential_id = table.Column<int>(type: "integer", nullable: true),
                    webhook_token = table.Column<string>(type: "text", nullable: true),
                    webhook_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    app_api_token = table.Column<string>(type: "text", nullable: true),
                    app_api_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    auto_deploy_mode = table.Column<string>(type: "text", nullable: false),
                    auto_deploy_time = table.Column<string>(type: "text", nullable: true),
                    last_deployed_commit = table.Column<string>(type: "text", nullable: true),
                    last_deployed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_deploy_status = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    backup_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    backup_cron = table.Column<string>(type: "text", nullable: true),
                    last_scheduled_backup_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    backup_stop_containers = table.Column<bool>(type: "boolean", nullable: false),
                    backup_quiesce_mode = table.Column<string>(type: "text", nullable: false, defaultValue: "Stop"),
                    template_id = table.Column<int>(type: "integer", nullable: true),
                    tenant_slug = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
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
                    table.ForeignKey(
                        name: "fk_stacks_stack_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "stack_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "user_recovery_codes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_recovery_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "routes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    target = table.Column<string>(type: "text", nullable: false, defaultValue: "Service"),
                    stack_id = table.Column<int>(type: "integer", nullable: true),
                    realm_id = table.Column<int>(type: "integer", nullable: true),
                    domain = table.Column<string>(type: "text", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    container_port = table.Column<int>(type: "integer", nullable: false),
                    tls_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    access_mode = table.Column<string>(type: "text", nullable: false, defaultValue: "Public"),
                    identity_header_mode = table.Column<string>(type: "text", nullable: false, defaultValue: "None"),
                    bypass_paths = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    status_detail = table.Column<string>(type: "text", nullable: true),
                    cert_not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_routes", x => x.id);
                    table.CheckConstraint("ck_routes_target", "(\"target\" = 'Watchtower' AND \"stack_id\" IS NULL AND \"realm_id\" IS NOT NULL AND \"access_mode\" = 'Public')\nOR (\"target\" = 'Service' AND \"stack_id\" IS NOT NULL AND \"realm_id\" IS NULL)");
                    table.ForeignKey(
                        name: "fk_routes_realms_realm_id",
                        column: x => x.realm_id,
                        principalTable: "realms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_routes_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stack_backup_service_overrides",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    stack_id = table.Column<int>(type: "integer", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    exclude = table.Column<bool>(type: "boolean", nullable: false),
                    stop = table.Column<string>(type: "text", nullable: true),
                    dump = table.Column<string>(type: "text", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "stack_env_vars",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    stack_id = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
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
                    stack_id = table.Column<int>(type: "integer", nullable: false),
                    has_updates = table.Column<bool>(type: "boolean", nullable: false),
                    outdated_images = table.Column<string>(type: "text", nullable: false),
                    outdated_image_digests = table.Column<string>(type: "text", nullable: false),
                    new_commit_sha = table.Column<string>(type: "text", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "template_management_grants",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    stack_id = table.Column<int>(type: "integer", nullable: false),
                    template_id = table.Column<int>(type: "integer", nullable: false),
                    allow_delete = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_template_management_grants", x => x.id);
                    table.ForeignKey(
                        name: "fk_template_management_grants_stack_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "stack_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_template_management_grants_stacks_stack_id",
                        column: x => x.stack_id,
                        principalTable: "stacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "route_access_grants",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    route_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    group_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_route_access_grants", x => x.id);
                    table.CheckConstraint("ck_route_access_grants_subject", "(\"user_id\" IS NOT NULL) <> (\"group_id\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_route_access_grants_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_route_access_grants_routes_route_id",
                        column: x => x.route_id,
                        principalTable: "routes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_route_access_grants_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "realms",
                columns: new[] { "id", "created_at", "is_system", "login_route_id", "name", "slug" },
                values: new object[] { 1, new DateTimeOffset(new DateTime(2026, 8, 10, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, null, "Operator", "operator" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_category",
                table: "audit_events",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_created_at",
                table: "audit_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_expires_at",
                table: "auth_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_route_id",
                table: "auth_sessions",
                column: "route_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_token_hash",
                table: "auth_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_user_id",
                table: "auth_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_backup_events_stack_id_started_at",
                table: "backup_events",
                columns: new[] { "stack_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_events_status",
                table: "backup_events",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_backup_paused_containers_container_id",
                table: "backup_paused_containers",
                column: "container_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_repos_credential_id",
                table: "ci_repos",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_ci_repos_owner_name",
                table: "ci_repos",
                columns: new[] { "owner", "name" },
                unique: true);

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
                name: "ix_group_members_group_id_user_id",
                table: "group_members",
                columns: new[] { "group_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_group_members_user_id",
                table: "group_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_groups_realm_id_normalized_name",
                table: "groups",
                columns: new[] { "realm_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_code_hash",
                table: "login_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_expires_at",
                table: "login_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_route_id",
                table: "login_codes",
                column: "route_id");

            migrationBuilder.CreateIndex(
                name: "ix_login_codes_user_id",
                table: "login_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_metric_container_samples_tier_seconds_t_unix_seconds_contai",
                table: "metric_container_samples",
                columns: new[] { "tier_seconds", "t_unix_seconds", "container_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_metric_host_samples_tier_seconds_t_unix_seconds",
                table: "metric_host_samples",
                columns: new[] { "tier_seconds", "t_unix_seconds" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_realms_login_route_id",
                table: "realms",
                column: "login_route_id",
                unique: true,
                filter: "\"login_route_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_realms_slug",
                table: "realms",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_registries_credential_id",
                table: "registries",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_registries_name",
                table: "registries",
                column: "name");

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

            migrationBuilder.CreateIndex(
                name: "ix_route_access_grants_user_id",
                table: "route_access_grants",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_routes_domain",
                table: "routes",
                column: "domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_routes_realm_id",
                table: "routes",
                column: "realm_id");

            migrationBuilder.CreateIndex(
                name: "ix_routes_stack_id",
                table: "routes",
                column: "stack_id");

            migrationBuilder.CreateIndex(
                name: "ix_stack_backup_service_overrides_stack_id_service",
                table: "stack_backup_service_overrides",
                columns: new[] { "stack_id", "service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stack_env_vars_stack_id_key",
                table: "stack_env_vars",
                columns: new[] { "stack_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stack_template_env_vars_template_id_key",
                table: "stack_template_env_vars",
                columns: new[] { "template_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stack_templates_credential_id",
                table: "stack_templates",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_stack_templates_name",
                table: "stack_templates",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stack_templates_realm_id",
                table: "stack_templates",
                column: "realm_id");

            migrationBuilder.CreateIndex(
                name: "ix_stacks_app_api_token",
                table: "stacks",
                column: "app_api_token",
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

            migrationBuilder.CreateIndex(
                name: "ix_stacks_template_id_tenant_slug",
                table: "stacks",
                columns: new[] { "template_id", "tenant_slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_management_grants_stack_id_template_id",
                table: "template_management_grants",
                columns: new[] { "stack_id", "template_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_template_management_grants_template_id",
                table: "template_management_grants",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_recovery_codes_user_id_code_hash",
                table: "user_recovery_codes",
                columns: new[] { "user_id", "code_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_realm_id_normalized_user_name",
                table: "users",
                columns: new[] { "realm_id", "normalized_user_name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_auth_sessions_routes_route_id",
                table: "auth_sessions",
                column: "route_id",
                principalTable: "routes",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_auth_sessions_users_user_id",
                table: "auth_sessions",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_backup_events_stacks_stack_id",
                table: "backup_events",
                column: "stack_id",
                principalTable: "stacks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_deploy_events_stacks_stack_id",
                table: "deploy_events",
                column: "stack_id",
                principalTable: "stacks",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_members_groups_group_id",
                table: "group_members",
                column: "group_id",
                principalTable: "groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_members_users_user_id",
                table: "group_members",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_groups_realms_realm_id",
                table: "groups",
                column: "realm_id",
                principalTable: "realms",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_login_codes_routes_route_id",
                table: "login_codes",
                column: "route_id",
                principalTable: "routes",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_login_codes_users_user_id",
                table: "login_codes",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_realms_routes_login_route_id",
                table: "realms",
                column: "login_route_id",
                principalTable: "routes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // (The seeded operator realm's identity sequence needs advancing past its explicit id, or the
            // first realm an administrator creates would collide with it. Npgsql's migration generator
            // emits that setval itself at the end of this migration, because it sees the InsertData
            // above land on an identity column — so there is nothing to do here.)

            // Two expression indexes EF cannot model, for the two lookups that compare lower(column)
            // against a lowered parameter. Without them each is a sequential scan, because a plain btree
            // on the column itself cannot answer a query over lower() of it. Not unique — the uniqueness
            // they support is a handler decision (StackProjectNames.IsTakenAsync reports a clash as a
            // validation error), and a case-insensitive unique constraint would be a stricter rule than
            // the one the application states. The matching configurations point back here.
            migrationBuilder.Sql(
                """
                CREATE INDEX "ix_stacks_compose_project_name_lower"
                    ON "stacks" (lower("compose_project_name"))
                """);
            migrationBuilder.Sql(
                """
                CREATE INDEX "ix_ci_repos_owner_name_lower"
                    ON "ci_repos" (lower("owner"), lower("name"))
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "ix_ci_repos_owner_name_lower" """);
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "ix_stacks_compose_project_name_lower" """);

            migrationBuilder.DropForeignKey(
                name: "fk_realms_routes_login_route_id",
                table: "realms");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "auth_sessions");

            migrationBuilder.DropTable(
                name: "backup_events");

            migrationBuilder.DropTable(
                name: "backup_paused_containers");

            migrationBuilder.DropTable(
                name: "ci_repos");

            migrationBuilder.DropTable(
                name: "deploy_events");

            migrationBuilder.DropTable(
                name: "elarion_settings");

            migrationBuilder.DropTable(
                name: "group_members");

            migrationBuilder.DropTable(
                name: "login_codes");

            migrationBuilder.DropTable(
                name: "metric_container_samples");

            migrationBuilder.DropTable(
                name: "metric_host_samples");

            migrationBuilder.DropTable(
                name: "registries");

            migrationBuilder.DropTable(
                name: "route_access_grants");

            migrationBuilder.DropTable(
                name: "stack_backup_service_overrides");

            migrationBuilder.DropTable(
                name: "stack_env_vars");

            migrationBuilder.DropTable(
                name: "stack_template_env_vars");

            migrationBuilder.DropTable(
                name: "stack_update_checks");

            migrationBuilder.DropTable(
                name: "template_management_grants");

            migrationBuilder.DropTable(
                name: "user_recovery_codes");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "routes");

            migrationBuilder.DropTable(
                name: "stacks");

            migrationBuilder.DropTable(
                name: "stack_templates");

            migrationBuilder.DropTable(
                name: "credentials");

            migrationBuilder.DropTable(
                name: "realms");
        }
    }
}
