using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProxyAuthState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "acme_accounts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    directory_url = table.Column<string>(type: "text", nullable: false),
                    private_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    protection = table.Column<string>(type: "text", nullable: false),
                    account_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acme_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "acme_http_challenges",
                columns: table => new
                {
                    token = table.Column<string>(type: "text", nullable: false),
                    key_authorization = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acme_http_challenges", x => x.token);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "elarion_role_leases",
                columns: table => new
                {
                    role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    address = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    expires_on_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_elarion_role_leases", x => x.role);
                });

            migrationBuilder.CreateTable(
                name: "elarion_scheduler_claims",
                columns: table => new
                {
                    job_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    occurrence_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_elarion_scheduler_claims", x => new { x.job_name, x.occurrence_utc });
                });

            migrationBuilder.CreateTable(
                name: "proxy_certificates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    host = table.Column<string>(type: "text", nullable: false),
                    certificate_pem = table.Column<string>(type: "text", nullable: false),
                    private_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    protection = table.Column<string>(type: "text", nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issuer = table.Column<string>(type: "text", nullable: false),
                    thumbprint = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proxy_certificates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "signing_keys",
                columns: table => new
                {
                    purpose = table.Column<string>(type: "text", nullable: false),
                    private_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    protection = table.Column<string>(type: "text", nullable: false),
                    key_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signing_keys", x => x.purpose);
                });

            migrationBuilder.CreateIndex(
                name: "ix_acme_accounts_directory_url",
                table: "acme_accounts",
                column: "directory_url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_acme_http_challenges_expires_at",
                table: "acme_http_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_elarion_scheduler_claims_purge",
                table: "elarion_scheduler_claims",
                column: "occurrence_utc");

            migrationBuilder.CreateIndex(
                name: "ix_proxy_certificates_host",
                table: "proxy_certificates",
                column: "host",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acme_accounts");

            migrationBuilder.DropTable(
                name: "acme_http_challenges");

            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "elarion_role_leases");

            migrationBuilder.DropTable(
                name: "elarion_scheduler_claims");

            migrationBuilder.DropTable(
                name: "proxy_certificates");

            migrationBuilder.DropTable(
                name: "signing_keys");
        }
    }
}
