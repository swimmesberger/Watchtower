using Elarion.Abstractions.Identity;

namespace Watchtower.Application.Services;

/// <summary>
/// The <see cref="ICurrentUser"/> used when <c>Auth:Enabled</c> is false: a local administrator that is
/// always authenticated. It exists so that turning secure-by-default authorization on
/// (<c>[assembly: ElarionAuthorizationDefaults]</c>) — which is unconditional, so the enabled path cannot
/// be forgotten on a handler — leaves the no-auth deployment behaving exactly as it did before, when
/// authentication was assumed to be the reverse proxy's job (see README).
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> the retired anonymous stand-in: an unauthenticated user would now be
/// denied by every handler. Claims are intentionally not carried — nothing in Watchtower declares
/// <c>[RequirePermission]</c>, and the interface's fail-closed defaults are the right answer if something
/// ever does, because "no authentication configured" must not silently satisfy a permission check.
/// </remarks>
public sealed class ImplicitAdminCurrentUser : ICurrentUser {
    /// <summary>
    /// The user id reported when authentication is switched off. The frontend keys its "sign out"
    /// affordance off this value — there is no session to end for an implicit user.
    /// </summary>
    public const string LocalUserId = "local";

    private static readonly string[] AdminOnly = [WatchtowerClaims.AdminRole];

    /// <inheritdoc />
    public string UserId => LocalUserId;

    /// <inheritdoc />
    public string? Email => null;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles => AdminOnly;

    /// <inheritdoc />
    public bool IsAuthenticated => true;

    /// <inheritdoc />
    public bool IsInRole(string role) => string.Equals(role, WatchtowerClaims.AdminRole, StringComparison.Ordinal);
}
