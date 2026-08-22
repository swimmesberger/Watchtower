using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The half of ADR-0021's one-time conversion that SQL cannot do: turning a configured
/// <c>Watchtower:Auth:Host</c> into the system realm's login <see cref="Route"/>.
/// </summary>
/// <remarks>
/// <para>
/// The other half — every realm's stored <c>auth_host</c> — is carried by the
/// <c>ConvertLoginHostsToRoutes</c> migration itself, because the column has to be read before it is
/// dropped and only the migration is running at that moment. <c>Auth:Host</c> is not a column: it lives in
/// the settings store, in an environment variable, or in <c>appsettings.json</c>, and no migration can see
/// any of those. So it is converted here, on the first start after the upgrade — and, unlike the migration
/// half, it writes audit rows, because by this point the audit trail exists and a scoped service can use
/// it.
/// </para>
/// <para>
/// <b>What it does.</b> When <c>Auth:Host</c> names a hostname and no route claims that hostname, a
/// Watchtower route is created for it in the system realm; when the system realm has no login route, the
/// Watchtower route for that hostname (the one just created, or one that was already there) becomes it. A
/// hostname already claimed by a <em>service</em> route is left entirely alone — the operator has said what
/// that hostname serves, and quietly re-pointing it at the management plane would be the worst possible
/// reading of an upgrade step.
/// </para>
/// <para>
/// <b>What it does not do.</b> It never clears <c>Auth:Host</c>. The setting keeps working as the system
/// realm's fallback (<see cref="RealmResolver.LoginHostForAsync"/>), so an instance fronted by somebody
/// else's proxy — where no route of ours is served at all — is not changed by this step in any way that
/// matters.
/// </para>
/// <para>
/// <b>It runs exactly once</b>, on the <see cref="WatchtowerSettingPaths.AuthLoginHostsConverted"/>
/// sentinel, and the sentinel is written on every path including the ones that create nothing. Without it
/// an operator who deliberately deleted the converted route would find it recreated on the next restart,
/// which is the opposite of what a one-time conversion means. Run from
/// <c>Program.InitializeDatabaseAsync</c> — after <c>Database.MigrateAsync</c>, because it reads and
/// writes the routes table, and before <c>app.RunAsync()</c>, because the proxy providers decide what to
/// serve in their <c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class LoginHostConversion(
    WatchtowerDbContext db,
    ISettingsManager settings,
    IOptionsMonitor<WatchtowerOptions> options,
    AuditLog audit,
    TimeProvider time,
    ILogger<LoginHostConversion> logger) {
    /// <summary>Audit category and action for the rows this step writes.</summary>
    internal const string AuditCategory = "proxy";

    /// <inheritdoc cref="AuditCategory"/>
    internal const string AuditAction = "route.convert";

    /// <summary>
    /// Converts the configured <c>Auth:Host</c> once and records that the question has been answered.
    /// Returns true when it changed something.
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken ct = default) {
        var converted = await settings.GetStringAsync(
            WatchtowerSettingPaths.AuthLoginHostsConverted, SettingsScope.Global, ct);
        if (!string.IsNullOrWhiteSpace(converted)) return false;

        var changed = await ConvertAsync(ct);

        // Written on every path, converted or not — an instance with no Auth:Host has answered the
        // question just as definitively as one that had a host to convert.
        await settings.SetStringAsync(
            WatchtowerSettingPaths.AuthLoginHostsConverted, "true",
            SettingsScope.Global, expectedVersion: null, ct);
        return changed;
    }

    private async Task<bool> ConvertAsync(CancellationToken ct) {
        var host = RouteAccessPolicy.NormalizeForwardedHost(options.CurrentValue.Auth.Host);
        if (host is null) return false;

        var system = await db.Realms.FirstOrDefaultAsync(r => r.IsSystem, ct);
        // A database with no system realm is broken in a way this step cannot fix and must not paper over
        // by inventing a population; RealmResolver throws about it at the first request either way.
        if (system is null) return false;

        var existing = await db.Routes.FirstOrDefaultAsync(r => r.Domain == host, ct);
        if (existing is { Target: RouteTarget.Service }) {
            logger.LogInformation(
                "Watchtower:Auth:Host is '{Host}', but that hostname is already a service route. Leaving " +
                "it alone: create a Watchtower route on another hostname and mark it as the operator " +
                "realm's login host.", host);
            return false;
        }
        // A Watchtower route on that hostname that belongs to *another* realm — which the migration half
        // can produce, since a realm's own auth_host is converted first. Re-pointing it at the operator
        // realm would take a customer population's login page away from it, and designating it as the
        // operator realm's login route would send operator visitors to a page that cannot admit them.
        // Neither is this step's call to make; the operator picks a hostname of their own instead.
        if (existing is not null && existing.RealmId != system.Id) {
            logger.LogWarning(
                "Watchtower:Auth:Host is '{Host}', but that hostname already serves Watchtower for " +
                "another realm. Leaving it alone: create a Watchtower route for the operator realm on " +
                "another hostname and mark it as its login host.", host);
            return false;
        }

        var changed = false;
        var route = existing;
        if (route is null) {
            route = new Route {
                Target = RouteTarget.Watchtower,
                StackId = null,
                RealmId = system.Id,
                Domain = host,
                ServiceName = string.Empty,
                ContainerPort = 0,
                TlsEnabled = true,
                Kind = DomainKind.Managed,
                AccessMode = AccessMode.Public,
                IdentityHeaderMode = IdentityHeaderMode.None,
                Status = RouteStatus.Pending,
                CreatedAt = time.GetUtcNow(),
            };
            db.Routes.Add(route);
            await db.SaveChangesAsync(ct);
            changed = true;
            // Actor-less on purpose: nobody asked for this, the upgrade did.
            await audit.RecordAsync(
                AuditCategory, AuditAction, host,
                "created a Watchtower route for the configured Auth:Host (ADR-0021)", ct: ct);
        }

        if (system.LoginRouteId is null) {
            system.LoginRouteId = route.Id;
            await db.SaveChangesAsync(ct);
            changed = true;
            await audit.RecordAsync(
                AuditCategory, AuditAction, host,
                "made it the operator realm's login route (ADR-0021)", ct: ct);
        }

        if (changed)
            logger.LogInformation("Converted Watchtower:Auth:Host '{Host}' into a Watchtower route.", host);
        return changed;
    }
}
