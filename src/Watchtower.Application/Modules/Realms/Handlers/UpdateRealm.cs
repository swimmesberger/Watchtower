using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Realms.Handlers;

/// <summary>
/// Renames a realm and moves its login host. The slug is <em>not</em> editable: it is the value of the
/// <c>realm</c> claim in every assertion the realm's applications receive, so changing it would silently
/// change what they are told about the population an account belongs to.
/// </summary>
/// <remarks>
/// Changing the login host moves the realm's cookie jar (docs/central-auth/design.md §13). Every
/// <c>__wt_sso</c> session on the old host is orphaned — the cookie is host-scoped, so the browser stops
/// presenting it — and the <c>iss</c> of every assertion the realm's apps receive changes with it. Both are
/// accepted rather than prevented: a realm that has to move hosts has to move hosts. The change is audited
/// so an operator reading the trail after "everyone was signed out" finds the reason.
/// <para>
/// The system realm is editable in name only: its login host is the configured <c>Auth:Host</c>, and
/// storing one on the row would create a second answer to "where does the operator log in" that
/// authentication would then have to read the database to resolve.
/// </para>
/// </remarks>
[Handler("realms.update")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class UpdateRealm(
    WatchtowerDbContext db,
    IOptionsMonitor<WatchtowerOptions> options,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<UpdateRealm.Command, Result<UpdateRealm.Response>> {

    /// <summary>
    /// Both editable fields are optional and both are "leave it alone" when omitted. <c>AuthHost</c> is
    /// therefore cleared by passing an empty string rather than by omitting it — omission cannot mean
    /// "remove", or a client that predates the field would silently unset every realm it saved.
    /// </summary>
    public sealed record Command(int Id, string? Name = null, string? AuthHost = null);
    public sealed record Response(RealmDto Realm);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var realm = await db.Realms.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (realm is null)
            return AppError.NotFound($"Realm {command.Id} not found.");

        var name = realm.Name;
        if (command.Name is not null) {
            if (RealmMapping.ValidateName(command.Name, out name) is { } badName) return badName;
        }

        var authHost = realm.AuthHost;
        if (command.AuthHost is not null) {
            if (RealmMapping.ValidateAuthHost(command.AuthHost, out authHost) is { } badHost) return badHost;

            if (realm.IsSystem && authHost is not null) {
                return AppError.Validation(
                    "The operator realm's login host is the configured Watchtower:Auth:Host and cannot be " +
                    "set here.");
            }

            if (authHost is not null) {
                if (RealmMapping.ValidateAuthHost(options.CurrentValue.Auth.Host, out var operatorHost) is null &&
                    string.Equals(authHost, operatorHost, StringComparison.Ordinal)) {
                    return AppError.Validation(
                        $"'{authHost}' is the Watchtower login host and cannot also be a realm's auth host.");
                }
                if (await db.Realms.AsNoTracking()
                        .AnyAsync(r => r.Id != realm.Id && r.AuthHost == authHost, ct)) {
                    return AppError.Conflict($"Another realm already uses the auth host '{authHost}'.");
                }
            }
        }

        // Read before the commit, not after: past the commit point the only permitted awaits are the
        // uncancellable ones (see RealmMapping.RecordAsync), and a count taken there would let a caller
        // that hangs up drop the audit row for a change that already happened.
        var userCount = await db.Users.CountAsync(u => u.RealmId == realm.Id, ct);
        var groupCount = await db.Groups.CountAsync(g => g.RealmId == realm.Id, ct);
        var templateCount = await db.StackTemplates.CountAsync(t => t.RealmId == realm.Id, ct);

        var previousName = realm.Name;
        var previousHost = realm.AuthHost;
        realm.Name = name;
        realm.AuthHost = authHost;
        await db.SaveChangesAsync(ct);

        // Past the commit point. The previous host is in the detail because it is where the sessions that
        // just stopped working were living — a row naming only the new one would not explain the change.
        var changes = string.Equals(previousHost, authHost, StringComparison.Ordinal)
            ? $"renamedFrom={previousName}"
            : $"renamedFrom={previousName}; authHost={previousHost ?? "(none)"}->{authHost ?? "(none)"}";
        await RealmMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.RealmUpdated, realm.Id, realm.Slug, changes);

        return new Response(RealmMapping.ToDto(realm, userCount, groupCount, templateCount));
    }
}
