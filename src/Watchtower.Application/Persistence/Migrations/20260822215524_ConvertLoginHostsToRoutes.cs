using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchtower.Application.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// ADR-0023, step two of two: the <em>data</em>. Every realm's stored <c>auth_host</c> becomes a
    /// <c>Watchtower</c>-target route in that realm, designated as its login route, and the column is then
    /// dropped.
    /// <para>
    /// <b>The conversion runs inside this migration, not in application code</b>, and that is the whole
    /// reason it is hand-edited: <c>realms.auth_host</c> has to be read <em>before</em> it is dropped, and
    /// the only moment both facts are true — <c>routes.stack_id</c> is already nullable (that was
    /// <c>AddRouteTargetAndLoginRoutes</c>) and the old column still exists — is here. The ordering holds
    /// because EF's SQLite generator hoists table rebuilds (the dropped column) to the end of the
    /// migration while raw SQL keeps its position.
    /// </para>
    /// <para>
    /// The <c>Auth:Host</c> half of the conversion is not a column at all — it lives in configuration or
    /// the settings store, which no migration can see — and is handled on the next start by
    /// <c>Services/LoginHostConversion.cs</c>.
    /// </para>
    /// </remarks>
    public partial class ConvertLoginHostsToRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One Watchtower route per realm that had an auth host.
            //
            // `NOT EXISTS` rather than a blind insert: a hostname already in the route table is one the
            // operator has said something about, and the unique index on routes.domain would refuse the
            // second claim anyway. When that existing row is a *service* route it is left exactly as it is
            // and the realm simply gets no login route — silently re-pointing a hostname that serves an
            // application at the management plane would be the worst possible reading of an upgrade, and
            // the UI can designate a different route in one click.
            //
            // created_at is the moment of the upgrade rather than a fixed literal (which is what
            // AddRealms' *seeded* row uses): this row is converted user data, not reference data, and it
            // genuinely came into existence now. The format is EF's own for a DateTimeOffset on SQLite
            // (`yyyy-MM-dd HH:mm:ss.fffffffzzz`), written out to the seven-digit fraction rather than
            // left to strftime's three, so a row this migration writes is byte-identical in shape to one
            // the application writes and sorts against it correctly as text.
            migrationBuilder.Sql(
                """
                INSERT INTO routes (
                    domain, target, realm_id, stack_id, service_name, container_port,
                    tls_enabled, is_primary, kind, access_mode, identity_header_mode,
                    bypass_paths, status, created_at)
                SELECT
                    lower(trim(r.auth_host)), 'Watchtower', r.id, NULL, '', 0,
                    1, 0, 'Managed', 'Public', 'None',
                    NULL, 'Pending', strftime('%Y-%m-%d %H:%M:%S.0000000+00:00', 'now')
                FROM realms r
                WHERE r.auth_host IS NOT NULL
                  AND trim(r.auth_host) <> ''
                  AND NOT EXISTS (
                      SELECT 1 FROM routes x WHERE lower(x.domain) = lower(trim(r.auth_host)));
                """);

            // Only a Watchtower route can be a login route, so a realm whose host was already taken by a
            // service route is skipped here by the target check rather than by a second WHERE clause.
            migrationBuilder.Sql(
                """
                UPDATE realms
                SET login_route_id = (
                    SELECT x.id FROM routes x
                    WHERE lower(x.domain) = lower(trim(realms.auth_host))
                      AND x.target = 'Watchtower')
                WHERE auth_host IS NOT NULL
                  AND trim(auth_host) <> ''
                  AND EXISTS (
                      SELECT 1 FROM routes x
                      WHERE lower(x.domain) = lower(trim(realms.auth_host))
                        AND x.target = 'Watchtower');
                """);

            migrationBuilder.DropIndex(
                name: "ix_realms_auth_host",
                table: "realms");

            migrationBuilder.DropColumn(
                name: "auth_host",
                table: "realms");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Best-effort, and lossy in one direction the old shape cannot express: it has no way to say
        /// "this hostname serves Watchtower", so each realm's login host is written back into
        /// <c>auth_host</c> and the Watchtower rows are then deleted. A realm's login host survives; a
        /// second Watchtower hostname (an operator's extra UI alias) does not, because there was never
        /// anywhere to keep one. The delete also has to happen here rather than in the previous
        /// migration's <c>Down</c>: that one makes <c>stack_id</c> NOT NULL again, which a stack-less row
        /// cannot satisfy.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "auth_host",
                table: "realms",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE realms
                SET auth_host = (SELECT lower(x.domain) FROM routes x WHERE x.id = realms.login_route_id)
                WHERE login_route_id IS NOT NULL;
                """);

            migrationBuilder.Sql("DELETE FROM routes WHERE target = 'Watchtower';");

            migrationBuilder.CreateIndex(
                name: "ix_realms_auth_host",
                table: "realms",
                column: "auth_host",
                unique: true,
                filter: "\"auth_host\" IS NOT NULL");
        }
    }
}
