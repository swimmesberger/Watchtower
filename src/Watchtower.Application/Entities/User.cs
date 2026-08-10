namespace Watchtower.Application.Entities;

/// <summary>
/// A local Watchtower account. Watchtower is the access-control plane for the apps it proxies
/// (docs/central-auth/design.md), and this is the identity it authenticates against by default.
/// </summary>
/// <remarks>
/// The shape mirrors what ASP.NET Identity <em>core</em> needs — password hash, security stamp and
/// the lockout counters — without pulling in <c>IdentityDbContext</c>: the store
/// (<see cref="Services.WatchtowerUserStore"/>) maps these properties onto
/// <see cref="Persistence.WatchtowerDbContext"/> directly.
/// </remarks>
public sealed class User {
    public int Id { get; set; }

    /// <summary>
    /// The population this account belongs to (design.md §13). Defaults to the system realm so every
    /// creation path that predates realms keeps producing an operator account; the handlers that may place
    /// an account elsewhere set it explicitly.
    /// </summary>
    public int RealmId { get; set; } = Realm.SystemRealmId;

    /// <inheritdoc cref="RealmId"/>
    public Realm? Realm { get; set; }

    /// <summary>
    /// Login name as the operator typed it. Unique <em>within the realm</em> (case-insensitively, via
    /// <see cref="NormalizedUserName"/>) — two populations may each have their own <c>admin</c>.
    /// </summary>
    public required string UserName { get; set; }

    /// <summary>
    /// Upper-cased form of <see cref="UserName"/> maintained by Identity's <c>ILookupNormalizer</c>.
    /// Lookups go through this column so user names are case-insensitive on SQLite too, and uniqueness is
    /// enforced on <c>(realm_id, normalized_user_name)</c>.
    /// </summary>
    public required string NormalizedUserName { get; set; }

    /// <summary>Optional contact address; surfaced to protected apps as <c>X-Watchtower-Email</c>. Not used for login.</summary>
    public string? Email { get; set; }

    /// <summary>PBKDF2 hash produced by Identity's <c>IPasswordHasher&lt;User&gt;</c>. Never a plaintext password.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Grants the <c>Admin</c> role: user management and system configuration.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>When true the account keeps existing but may neither log in nor pass access verification.</summary>
    public bool Disabled { get; set; }

    /// <summary>Consecutive failed login attempts; reset on success and by an administrative unlock.</summary>
    public int AccessFailedCount { get; set; }

    /// <summary>When set and in the future, logins are refused until this moment.</summary>
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>
    /// Random value regenerated whenever credentials change. Sessions minted before the change can be
    /// recognised as stale, which is also the hook the planned MFA work builds on.
    /// </summary>
    public required string SecurityStamp { get; set; }

    /// <summary>
    /// EF Core optimistic-concurrency token, rotated on every write by
    /// <see cref="Services.WatchtowerUserStore"/>. Two administrators editing the same account
    /// concurrently make the second write fail loudly instead of silently discarding the first.
    /// </summary>
    public required string ConcurrencyStamp { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
