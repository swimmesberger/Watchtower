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
    /// "Anything else" deliberately includes the configured <c>Auth:Host</c>, the published port and bare-IP
    /// access — see the class remarks. The comparison is over the normalised host
    /// (<see cref="RouteAccessPolicy.NormalizeForwardedHost"/>), so a value carrying a port, a path or
    /// userinfo never matches a realm; it is not a host name, and coercing it into one is exactly how a
    /// caller would try to be handed the wrong population's login.
    /// </remarks>
    public async Task<Realm> ResolveByHostAsync(string? host, CancellationToken ct) {
        var normalized = RouteAccessPolicy.NormalizeForwardedHost(host);
        if (normalized is null) return await SystemRealmAsync(ct);

        var realm = await db.Realms.AsNoTracking()
            .FirstOrDefaultAsync(r => !r.IsSystem && r.AuthHost != null && r.AuthHost.ToLower() == normalized, ct);
        return realm ?? await SystemRealmAsync(ct);
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
    /// The system realm's host is always the configured <c>Watchtower:Auth:Host</c> and never
    /// <see cref="Realm.AuthHost"/> (which handlers keep null on it): authentication must not need a
    /// database row to find its own login page. A non-system realm created before its DNS exists has no
    /// host, and its protected routes then fail closed at challenge time rather than redirecting somewhere
    /// arbitrary.
    /// </remarks>
    public string? LoginHostFor(Realm realm) {
        ArgumentNullException.ThrowIfNull(realm);
        return realm.IsSystem
            ? RouteAccessPolicy.NormalizeForwardedHost(options.CurrentValue.Auth.Host)
            : RouteAccessPolicy.NormalizeForwardedHost(realm.AuthHost);
    }

    /// <summary>
    /// Every non-system realm's non-null <see cref="Realm.AuthHost"/>, lowercased — the extra site blocks
    /// the proxy has to serve so each realm's login page is reachable
    /// (<see cref="CaddyManager.ProjectSites"/>).
    /// </summary>
    /// <remarks>
    /// The system realm is excluded here exactly as it is in <see cref="ResolveByHostAsync"/>: its login
    /// host is the configured <c>Auth:Host</c>, which the projection already adds, and a stored one on that
    /// row would be a second answer neither reads.
    /// </remarks>
    public async Task<IReadOnlyList<string>> AuthHostsAsync(CancellationToken ct) =>
        await db.Realms.AsNoTracking()
            .Where(r => !r.IsSystem && r.AuthHost != null)
            .Select(r => r.AuthHost!.ToLower())
            .ToListAsync(ct);

    /// <summary>All realms, ordered by slug.</summary>
    public async Task<IReadOnlyList<Realm>> ListAsync(CancellationToken ct) =>
        await db.Realms.AsNoTracking().OrderBy(r => r.Slug).ToListAsync(ct);

    /// <summary>
    /// Every realm's <c>iss</c>, mapped to the realm it identifies — what a surface that has no realm in
    /// context (UserInfo) validates an assertion against, and then checks the resolved account's realm
    /// against.
    /// </summary>
    /// <remarks>
    /// One key pair signs every realm's assertions (per-realm keys are not a v1 feature), so the issuer is
    /// the only thing in a token that says which population it is about. Two realms should not be able to
    /// share one — the login host is unique among realms and the handlers refuse a realm host equal to the
    /// configured operator one — but <c>Auth:Host</c> is configuration and can be changed <em>after</em> a
    /// realm has taken that host, which no handler is in a position to refuse. The first realm wins and the
    /// collision is logged rather than thrown: the auth path must keep answering, and the loser's tokens
    /// are refused by the caller's own realm check rather than silently accepted as the winner's. The
    /// warning is what turns "one realm's users mysteriously cannot use UserInfo" into a fixable line.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, int>> IssuersAsync(CancellationToken ct) {
        var realms = await ListAsync(ct);
        var issuers = new Dictionary<string, int>(realms.Count, StringComparer.Ordinal);
        foreach (var realm in realms) {
            var issuer = signer.IssuerFor(RealmIdentity.From(realm));
            if (issuers.TryAdd(issuer, realm.Id)) continue;
            logger.LogWarning(
                "Realms {WinningRealmId} and {LosingRealm} both resolve to the token issuer '{Issuer}', so " +
                "assertions minted for the second cannot be attributed to it. Change Watchtower:Auth:Host " +
                "or the realm's auth host so each population has its own.",
                issuers[issuer], realm.Slug, issuer);
        }
        return issuers;
    }
}
