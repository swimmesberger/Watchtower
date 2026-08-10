using System.Globalization;
using System.Security.Claims;
using Elarion.Abstractions.Identity;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>
/// The claim vocabulary shared by the two ends of Watchtower's native login: the authentication handler
/// that mints the principal (<c>Watchtower.Api</c>) and the claims-to-<c>ICurrentUser</c> mapping the
/// application layer configures. Both sides read these constants so the mapping cannot silently drift —
/// a mismatch would not fail the build, it would quietly produce an authenticated user with no id or role.
/// </summary>
public static class WatchtowerClaims {
    /// <summary>Claim carrying <see cref="Entities.User.Id"/> — what <c>ICurrentUser.UserId</c> resolves to.</summary>
    public const string UserId = ClaimTypes.NameIdentifier;

    /// <summary>Claim carrying <see cref="Entities.User.UserName"/> — the display name.</summary>
    public const string Name = ClaimTypes.Name;

    /// <summary>Claim carrying <see cref="Entities.User.Email"/>, when the account has one.</summary>
    public const string Email = ClaimTypes.Email;

    /// <summary>Claim type roles are emitted under.</summary>
    public const string Role = ClaimTypes.Role;

    /// <summary>
    /// Claim carrying the <see cref="Realm.Slug"/> of the population the account belongs to
    /// (docs/central-auth/design.md §13). Always emitted for an authenticated principal, and the value the
    /// management surface's system-realm gate is decided on — a slug is immutable and unique, so it names
    /// exactly one realm for the lifetime of the instance.
    /// </summary>
    public const string RealmSlug = "watchtower:realm";

    /// <summary>The single role in v1: user management and system configuration (<see cref="Entities.User.IsAdmin"/>).</summary>
    public const string AdminRole = "Admin";

    /// <summary>
    /// The claims an authenticated principal carries, in one place so every mint produces the same shape.
    /// </summary>
    /// <remarks>
    /// The Admin role is emitted <em>only</em> for a system-realm account, whatever
    /// <see cref="Entities.User.IsAdmin"/> says. The user handlers already refuse to set that flag outside
    /// the operator realm, so this is the belt to that braces: were a row ever to acquire it — by a
    /// direct database edit, or by a realm move some later feature adds — it would still not turn into
    /// administrative authority over the instance.
    /// </remarks>
    /// <param name="user">The authenticated account.</param>
    /// <param name="realm">Its realm, which decides the realm claim and gates the role.</param>
    public static List<Claim> ForUser(User user, Realm realm) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(realm);

        var claims = new List<Claim>(5) {
            new(UserId, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(Name, user.UserName),
            new(RealmSlug, realm.Slug),
        };
        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim(Email, user.Email));
        if (user.IsAdmin && realm.IsSystem)
            claims.Add(new Claim(Role, AdminRole));

        return claims;
    }

    /// <summary>
    /// Whether <paramref name="user"/> belongs to the built-in operator realm — the question the
    /// management surface is gated on (design.md §13, and <see cref="SystemRealmAuthorizer"/>).
    /// </summary>
    /// <remarks>
    /// Answered from the claim rather than from a database read, because it has to be answerable in the
    /// authorization pipeline, before any handler runs, on every transport. A principal carrying no realm
    /// claim at all is not in the system realm: fail-closed, so a future authentication path that forgets
    /// to state the realm loses the management surface rather than handing it out.
    /// </remarks>
    public static bool IsSystemRealm(ICurrentUser user) {
        ArgumentNullException.ThrowIfNull(user);
        return user.HasClaim(RealmSlug, Realm.SystemRealmSlug);
    }
}
