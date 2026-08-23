using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Realms.Handlers;

/// <summary>
/// Renames a realm and moves its login host. The slug is <em>not</em> editable: it is the value of the
/// <c>realm</c> claim in every assertion the realm's applications receive, so changing it would silently
/// change what they are told about the population an account belongs to.
/// </summary>
/// <remarks>
/// Changing the login route moves the realm's cookie jar (docs/central-auth/design.md §13). Every
/// <c>__wt_sso</c> session on the old host is orphaned — the cookie is host-scoped, so the browser stops
/// presenting it — and the <c>iss</c> of every assertion the realm's apps receive changes with it. Both are
/// accepted rather than prevented: a realm that has to move hosts has to move hosts. The change is audited
/// so an operator reading the trail after "everyone was signed out" finds the reason.
/// <para>
/// The system realm is editable here like any other (ADR-0023): its login host is a Watchtower route too,
/// and the configured <c>Auth:Host</c> is only the fallback used when it has none.
/// </para>
/// </remarks>
[Handler("realms.update")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class UpdateRealm(
    WatchtowerDbContext db,
    IProxyProvider proxy,
    RealmResolver realms,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<UpdateRealm.Command, Result<UpdateRealm.Response>> {

    /// <summary>
    /// Both editable fields are optional and both are "leave it alone" when omitted.
    /// <c>LoginRouteId</c> is therefore cleared by passing <c>0</c> rather than by omitting it — omission
    /// cannot mean "remove", or a client that predates the field would silently unset every realm it saved.
    /// </summary>
    public sealed record Command(int Id, string? Name = null, int? LoginRouteId = null);
    public sealed record Response(RealmDto Realm);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(command);

        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (realm is null)
            return AppError.NotFound($"Realm {command.Id} not found.");

        var name = realm.Name;
        if (command.Name is not null) {
            if (RealmMapping.ValidateName(command.Name, out name) is { } badName) return badName;
        }

        var loginRouteId = realm.LoginRouteId;
        if (command.LoginRouteId is { } requested) {
            if (requested == 0) {
                loginRouteId = null;
            } else {
                // Only one of the realm's own Watchtower routes. A service route cannot serve a login
                // page at all, and another realm's would make one hostname resolve to two populations.
                var route = await db.Routes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requested, ct);
                if (route is null || route.Target != RouteTarget.Watchtower || route.RealmId != realm.Id) {
                    return AppError.Validation(
                        $"Route {requested} is not a Watchtower route of realm '{realm.Slug}'.");
                }
                loginRouteId = route.Id;
            }
        }

        // Read before the commit, not after: past the commit point the only permitted awaits are the
        // uncancellable ones (see RealmMapping.RecordAsync), and a count taken there would let a caller
        // that hangs up drop the audit row for a change that already happened.
        var userCount = await db.Users.CountAsync(u => u.RealmId == realm.Id, ct);
        var groupCount = await db.Groups.CountAsync(g => g.RealmId == realm.Id, ct);
        var templateCount = await db.StackTemplates.CountAsync(t => t.RealmId == realm.Id, ct);

        var previousName = realm.Name;
        var previousHost = await realms.LoginHostForAsync(realm, ct);
        realm.Name = name;
        realm.LoginRouteId = loginRouteId;
        await db.SaveChangesAsync(ct);

        // Which hostname the realm's visitors are redirected to has changed. Best-effort like the route
        // CRUD handlers; the site list itself does not move (the routes were already there), but the
        // reconcile is what makes a newly designated host's certificate start issuing.
        await proxy.ApplyAsync(ct);

        var loginHost = await realms.LoginHostForAsync(realm, ct);

        // Past the commit point. The detail names only what actually changed — a host-only save must not
        // claim a rename — and the previous host is included because it is where the sessions that just
        // stopped working were living; a row naming only the new one would not explain the change.
        var parts = new List<string>(2);
        if (!string.Equals(previousName, name, StringComparison.Ordinal))
            parts.Add($"renamedFrom={previousName}");
        if (!string.Equals(previousHost, loginHost, StringComparison.Ordinal))
            parts.Add($"loginHost={previousHost ?? "(none)"}->{loginHost ?? "(none)"}");
        var changes = parts.Count > 0 ? string.Join("; ", parts) : "no changes";
        await RealmMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.RealmUpdated, realm.Id, realm.Slug, changes);

        return new Response(RealmMapping.ToDto(realm, userCount, groupCount, templateCount, loginHost));
    }
}
