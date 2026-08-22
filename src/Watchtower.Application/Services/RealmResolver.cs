using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

// Watchtower's own entity, not Microsoft.AspNetCore.Routing.Route.
using Route = Watchtower.Application.Entities.Route;

namespace Watchtower.Application.Services;

/// <summary>
/// The single place a realm is decided (docs/central-auth/design.md §13): which population a request's host
/// belongs to, which one a route's tenants serve, and which host each realm's login page lives on. Every
/// endpoint and service that needs a realm asks here rather than parsing hosts of its own, for the same
/// reason <see cref="RouteAccessPolicy"/> exists — three surfaces that answer the question separately would
/// eventually answer it differently, and a disagreement about which population a visitor is in is a hole,
/// not a bug.
/// </summary>
/// <remarks>
/// Scoped, like the context it reads through. Everything here is fail-<em>safe</em> rather than
/// fail-closed in one specific direction: an unrecognised host resolves to the <em>system</em> realm, so a
/// request that arrives on the published port, by IP, or on the configured <c>Auth:Host</c> lands on the
/// operator login instead of nowhere. That is deliberate — the failure mode of guessing wrong here is a
/// lockout, and the operator population is the one that can always fix it. It costs nothing: an account
/// still only ever authenticates within its own realm (<see cref="IRealmContext"/>), so resolving to the
/// system realm does not let a realm account in anywhere, it only decides which login page they see.
/// </remarks>
public sealed class RealmResolver(
    WatchtowerDbContext db,
    IOptionsMonitor<WatchtowerOptions> options,
    AuthTokenSigner signer,
    ILogger<RealmResolver> logger) {
    /// <summary>The built-in operator realm — the <see cref="Realm.IsSystem"/> row, seeded by the migration.</summary>
    /// <remarks>
    /// Read rather than assumed from <see cref="Realm.SystemRealmId"/>: the constant is what column defaults
    /// point at, this is what decisions are made on.
    /// </remarks>
    public async Task<Realm> SystemRealmAsync(CancellationToken ct) {
        var realm = await db.Realms.AsNoTracking().FirstOrDefaultAsync(r => r.IsSystem, ct);
        // The migration seeds it and realms.delete refuses to remove it, so its absence is a broken
        // database rather than a state to recover from — and silently inventing one would put every
        // account in a realm that is not stored anywhere.
        return realm ?? throw new InvalidOperationException(
            "The system realm is missing from the database. It is seeded by the AddRealms migration and " +
            "cannot be deleted; restore the database or re-apply migrations.");
    }

    /// <summary>
    /// The realm a request arriving on <paramref name="host"/> authenticates into: the realm that claims
    /// that login host, or the system realm for anything else.
    /// </summary>
    /// <remarks>
    /// The host is looked up in the route table: a <see cref="RouteTarget.Watchtower"/> route names the
    /// realm whose surface it serves (ADR-0021), so the same rows the proxy serves are what decides the
    /// population. The configured <c>Auth:Host</c> resolves to the system realm — it is that realm's
    /// fallback login host — and so does everything else: the published port and bare-IP access, see the
    /// class remarks. The comparison is over the normalised host
    /// (<see cref="RouteAccessPolicy.NormalizeForwardedHost"/>), so a value carrying a port, a path or
    /// userinfo never matches a route; it is not a host name, and coercing it into one is exactly how a
    /// caller would try to be handed the wrong population's login.
    /// </remarks>
    public async Task<Realm> ResolveByHostAsync(string? host, CancellationToken ct) {
        var normalized = RouteAccessPolicy.NormalizeForwardedHost(host);
        if (normalized is null) return await SystemRealmAsync(ct);

        var realmId = await db.Routes.AsNoTracking()
            .Where(r => r.Target == RouteTarget.Watchtower && r.Domain.ToLower() == normalized)
            .Select(r => r.RealmId)
            .FirstOrDefaultAsync(ct);
        if (realmId is { } id && await FindAsync(id, ct) is { } realm) return realm;

        return await SystemRealmAsync(ct);
    }

    /// <summary>The realm by id, or <see langword="null"/> when there is no such row.</summary>
    public Task<Realm?> FindAsync(int realmId, CancellationToken ct) =>
        db.Realms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == realmId, ct);

    /// <summary>
    /// The realm <paramref name="route"/> belongs to: its stack's category
    /// (<see cref="StackTemplate.RealmId"/>), or the system realm when the stack is standalone.
    /// </summary>
    public async Task<Realm> RealmForRouteAsync(Route route, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(route);
        var realmId = await RouteAccessPolicy.RouteRealmIdAsync(db, route.Id, ct);
        // A route that has vanished between being read and being asked about resolves to the operator
        // realm — the same fail-safe direction as an unknown host, and the caller has already established
        // the route exists in every path that reaches here.
        if (realmId is null) return await SystemRealmAsync(ct);
        return await FindAsync(realmId.Value, ct) ?? await SystemRealmAsync(ct);
    }

    /// <summary>
    /// The host <paramref name="realm"/>'s login page is served on, or <see langword="null"/> when it has
    /// none yet.
    /// </summary>
    /// <remarks>
    /// The realm's <see cref="Realm.LoginRouteId"/> is the answer: one of its
    /// <see cref="RouteTarget.Watchtower"/> routes, designated as the address protected apps redirect to
    /// (ADR-0021). A realm created before its DNS exists has none, and its protected routes then fail
    /// closed at challenge time rather than redirecting somewhere arbitrary.
    /// <para>
    /// The system realm — and only the system realm — falls back to the configured
    /// <c>Watchtower:Auth:Host</c>. That is the "Watchtower sits behind somebody else's proxy" case: no
    /// provider of ours serves the hostname, so there is nothing for a route row to do except carry the
    /// name, and an operator who prefers to state it in configuration can. A non-system realm in that
    /// position creates a Watchtower route instead — unserved while the proxy is off, but still the one
    /// place its login address is written down.
    /// </para>
    /// </remarks>
    public async Task<string?> LoginHostForAsync(Realm realm, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(realm);

        // The navigation when it was loaded, a query when it was not: callers reach this from both a
        // freshly-read realm and one that came out of an AsNoTracking projection.
        var domain = realm.LoginRoute?.Domain;
        if (domain is null && realm.LoginRouteId is { } routeId) {
            domain = await db.Routes.AsNoTracking()
                .Where(r => r.Id == routeId)
                .Select(r => r.Domain)
                .FirstOrDefaultAsync(ct);
        }

        return RouteAccessPolicy.NormalizeForwardedHost(domain)
               ?? (realm.IsSystem ? RouteAccessPolicy.NormalizeForwardedHost(options.CurrentValue.Auth.Host) : null);
    }

    /// <summary>
    /// The realm as the token signer sees it: its slug, whether it is the operator population, and the
    /// login host that decides its <c>iss</c> (<see cref="LoginHostForAsync"/>).
    /// </summary>
    /// <remarks>
    /// The one place the two halves are put together, so "which issuer does this realm mint under" cannot
    /// be answered from a stale host by one caller and the current one by another.
    /// </remarks>
    public async Task<RealmIdentity> IdentityForAsync(Realm realm, CancellationToken ct) =>
        RealmIdentity.From(realm, await LoginHostForAsync(realm, ct));

    /// <summary>All realms, ordered by slug, with their login route loaded.</summary>
    /// <remarks>
    /// The login route comes along because nearly every caller goes on to ask for the login host, and one
    /// query with an <c>Include</c> beats one per realm.
    /// </remarks>
    public async Task<IReadOnlyList<Realm>> ListAsync(CancellationToken ct) =>
        await db.Realms.AsNoTracking().Include(r => r.LoginRoute).OrderBy(r => r.Slug).ToListAsync(ct);

    /// <summary>
    /// Every realm's <c>iss</c>, mapped to the realm it identifies — what a surface that has no realm in
    /// context (UserInfo) validates an assertion against, and then checks the resolved account's realm
    /// against.
    /// </summary>
    /// <remarks>
    /// One key pair signs every realm's assertions (per-realm keys are not a v1 feature), so the issuer is
    /// the only thing in a token that says which population it is about. Two realms should not be able to
    /// share one — a login host is a route domain and those are unique, and a realm may only name one of
    /// <em>its own</em> Watchtower routes — but <c>Auth:Host</c> is configuration and can be pointed at a
    /// realm's login host <em>after</em> the fact, which no handler is in a position to refuse. The first realm wins and the
    /// collision is logged rather than thrown: the auth path must keep answering, and the loser's tokens
    /// are refused by the caller's own realm check rather than silently accepted as the winner's. The
    /// warning is what turns "one realm's users mysteriously cannot use UserInfo" into a fixable line.
    /// <para>
    /// Which realm is "first" is decided here rather than inherited from <see cref="ListAsync"/>: on its
    /// slug order the operator realm would win a collision only because <c>operator</c> happens to sort
    /// early, and a customer realm slugged <c>acme</c> would take the operator issuer instead — the one
    /// outcome that must not be reachable, since the losing population is then the one that administers
    /// this instance. Ordering the system realm first makes it structurally unable to lose, so the tie-break
    /// no longer depends on a name somebody else chooses.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, int>> IssuersAsync(CancellationToken ct) {
        var realms = await ListAsync(ct);
        var issuers = new Dictionary<string, int>(realms.Count, StringComparer.Ordinal);
        // Stable within each group (OrderByDescending is a stable sort), so the customer realms keep the
        // slug order ListAsync established and only the operator one is lifted out of it.
        foreach (var realm in realms.OrderByDescending(r => r.IsSystem)) {
            var issuer = signer.IssuerFor(await IdentityForAsync(realm, ct));
            if (issuers.TryAdd(issuer, realm.Id)) continue;
            logger.LogWarning(
                "Realms {WinningRealmId} and {LosingRealm} both resolve to the token issuer '{Issuer}', so " +
                "assertions minted for the second cannot be attributed to it. Change Watchtower:Auth:Host " +
                "or the realm's login route so each population has its own.",
                issuers[issuer], realm.Slug, issuer);
        }
        return issuers;
    }
}
