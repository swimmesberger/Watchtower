using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Realms.Handlers;

/// <summary>
/// Creates an empty realm. It holds nobody until accounts are created in it and reaches nothing until a
/// category is placed in it, so creating one grants no access by itself.
/// </summary>
/// <remarks>
/// The login host is optional because DNS usually is not ready yet: a realm without one is a perfectly
/// good population that simply cannot be logged into, and its protected routes fail closed at challenge
/// time until it has one (see <c>WatchtowerAccessEndpoints</c>). Giving one here creates the
/// <see cref="RouteTarget.Watchtower"/> route for the hostname and designates it (ADR-0023).
/// <para>
/// <b>There is deliberately no way to name an <em>existing</em> route at creation.</b> A Watchtower route
/// carries the realm it serves, so no route can already belong to a realm that does not exist yet —
/// the option would have been unsatisfiable by construction. Designating one is
/// <c>realms.update</c>'s job, once both the realm and the route exist.
/// </para>
/// <para>
/// The uniqueness checks are reads followed by an insert, and the unique indexes on <c>realms.slug</c>,
/// <c>realms.login_route_id</c> and <c>routes.domain</c> are what actually settle a race between two
/// administrators — the checks exist to turn the common case into a clear <c>Conflict</c> rather than a
/// database exception.
/// </para>
/// </remarks>
[Handler("realms.create")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class CreateRealm(
    WatchtowerDbContext db,
    IProxyProvider proxy,
    RealmResolver realms,
    IOptionsMonitor<WatchtowerOptions> options,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<CreateRealm.Command, Result<CreateRealm.Response>> {

    /// <param name="LoginDomain">Hostname to create a Watchtower route for and use as the login host.</param>
    public sealed record Command(string Name, string Slug, string? LoginDomain = null);
    public sealed record Response(RealmDto Realm);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(command);

        if (RealmMapping.ValidateName(command.Name, out var name) is { } badName)
            return badName;
        if (RealmMapping.ValidateSlug(command.Slug, out var slug) is { } badSlug)
            return badSlug;
        if (RealmMapping.ValidateLoginDomain(command.LoginDomain, out var loginDomain) is { } badHost)
            return badHost;

        if (await db.Realms.AsNoTracking().AnyAsync(r => r.Slug == slug, ct))
            return AppError.Conflict($"A realm with the slug '{slug}' already exists.");
        if (loginDomain is not null) {
            if (await db.Routes.AsNoTracking().AnyAsync(r => r.Domain == loginDomain, ct))
                return AppError.Conflict($"Domain '{loginDomain}' is already routed.");
            // The same collision proxy.createRoute refuses, checked before the realm row is written so a
            // refusal here leaves nothing behind. `IsSystem` is false by construction below.
            var clash = RouteMapping.CheckAuthHostCollision(
                loginDomain, new Realm { Name = name, Slug = slug }, options.CurrentValue.Auth.Host);
            if (clash is not null) return clash;
        }

        var realm = new Realm {
            Name = name,
            Slug = slug,
            // Never settable from a request: the system realm is the one the migration seeded, and a second
            // one would make "the operator population" ambiguous.
            IsSystem = false,
            CreatedAt = time.GetUtcNow(),
        };
        db.Realms.Add(realm);
        await db.SaveChangesAsync(ct);

        if (loginDomain is not null) {
            var route = NewLoginRoute(loginDomain, realm.Id, time.GetUtcNow());
            db.Routes.Add(route);
            await db.SaveChangesAsync(ct);
            realm.LoginRouteId = route.Id;
            await db.SaveChangesAsync(ct);
        }

        // The realm's login host is a hostname the proxy has to serve, so the generated configuration
        // changed — reload it, best-effort like the route CRUD handlers. Without this the new login page
        // is not served until some unrelated reconcile happens.
        await proxy.ApplyAsync(ct);

        var loginHost = await realms.LoginHostForAsync(realm, ct);

        // Past the commit point: the realm exists, so the trail is written uncancellably.
        await RealmMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.RealmCreated, realm.Id, realm.Slug,
            $"loginHost={loginHost ?? "(none)"}");

        return new Response(RealmMapping.ToDto(
            realm, userCount: 0, groupCount: 0, templateCount: 0, loginHost));
    }

    /// <summary>
    /// The Watchtower route a <c>loginDomain</c> turns into. TLS on and <see cref="DomainKind.Managed"/>:
    /// the same defaults <c>proxy.createRoute</c> offers, and the ones an operator naming a login host
    /// almost always wants — a login page reached over plain HTTP would set the session cookie without its
    /// <c>Secure</c> attribute.
    /// </summary>
    internal static Route NewLoginRoute(string domain, int realmId, DateTimeOffset createdAt) => new() {
        Target = RouteTarget.Watchtower,
        StackId = null,
        RealmId = realmId,
        Domain = domain,
        ServiceName = string.Empty,
        ContainerPort = 0,
        TlsEnabled = true,
        Kind = DomainKind.Managed,
        AccessMode = AccessMode.Public,
        IdentityHeaderMode = IdentityHeaderMode.None,
        Status = RouteStatus.Pending,
        CreatedAt = createdAt,
    };
}
