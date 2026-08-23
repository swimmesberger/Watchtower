namespace Watchtower.Application.Entities;

/// <summary>
/// A user population and everything that is scoped to one (docs/central-auth/design.md §13): its own
/// credential space (user and group names are unique <em>within</em> a realm, not globally), its own SSO
/// scope, and the categories (<see cref="StackTemplate"/>s) whose tenants its accounts may enter.
/// </summary>
/// <remarks>
/// Exactly one row is the <see cref="IsSystem"/> realm — the operator population, seeded by the
/// <c>AddRealms</c> migration, which owns Watchtower's own management surface and is the realm every
/// pre-realm row was backfilled into. It cannot be deleted, and it is the only realm that falls back to
/// the configured <c>Watchtower:Auth:Host</c> when it has no <see cref="LoginRouteId"/> — the escape
/// hatch for an instance fronted by somebody else's proxy (ADR-0023).
/// <para>
/// Per-population settings — password policy, lockout, future MFA and federation config — belong on this
/// entity as they land (design.md §13, seam 3). None of them exist yet; today's are still instance-wide
/// <c>Auth:*</c> options.
/// </para>
/// </remarks>
public sealed class Realm {
    /// <summary>
    /// Primary key of the built-in system realm. Seeded with this explicit id by the <c>AddRealms</c>
    /// migration, which is what lets the realm columns added to existing tables default to it and what
    /// makes <see cref="Services.IRealmContext"/>'s default a constant rather than a query.
    /// <see cref="Services.RealmResolver"/> is still the authority — it reads the <see cref="IsSystem"/>
    /// row — so nothing but a default ever assumes the number.
    /// </summary>
    public const int SystemRealmId = 1;

    /// <summary>
    /// <see cref="Slug"/> of the system realm. Immutable (slugs are), unique (the index says so), and
    /// therefore usable as the value of the realm claim the management-surface gate checks.
    /// </summary>
    public const string SystemRealmSlug = "operator";

    /// <summary><see cref="Name"/> the system realm is seeded with. Editable afterwards; only the slug is fixed.</summary>
    public const string SystemRealmName = "Operator";

    /// <summary>Longest accepted <see cref="Slug"/> — one DNS label, which is what a slug may have to become.</summary>
    public const int MaxSlugLength = 63;

    /// <summary>Longest accepted <see cref="Name"/>. Long enough for a readable label, short enough to stay one.</summary>
    public const int MaxNameLength = 64;

    public int Id { get; set; }

    /// <summary>Display name as the administrator typed it. Not an identifier — <see cref="Slug"/> is.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Stable machine identifier, unique across realms and <b>immutable after creation</b>: it is the value
    /// of the <c>realm</c> claim in every forwarded assertion, so renaming it would silently change what
    /// protected upstreams are told about the population an account belongs to.
    /// </summary>
    public required string Slug { get; set; }

    /// <summary>
    /// The <see cref="RouteTarget.Watchtower"/> route whose domain this realm's login page and
    /// <c>__wt_sso</c> cookie live on — the realm's SSO scope, since a host-scoped cookie <em>is</em> the
    /// cookie jar (design.md §13, ADR-0023). Null while the realm has no login host yet: its protected
    /// routes then fail closed at challenge time rather than redirecting anywhere — except on the system
    /// realm, which falls back to the configured <c>Auth:Host</c>.
    /// </summary>
    /// <remarks>
    /// A route rather than a stored hostname, so there is one table of "hostnames this instance answers
    /// on" and one place per provider that serves them. The foreign key is <c>ON DELETE SET NULL</c>:
    /// deleting the route is a legitimate act with a visible consequence (the realm has no login host any
    /// more), not something to refuse, and the handler says so in its response.
    /// </remarks>
    public int? LoginRouteId { get; set; }

    /// <summary>Navigation to <see cref="LoginRouteId"/>; loaded where the login host is needed.</summary>
    public Route? LoginRoute { get; set; }

    /// <summary>True for the single built-in operator realm. Set by the migration; never by a handler.</summary>
    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
