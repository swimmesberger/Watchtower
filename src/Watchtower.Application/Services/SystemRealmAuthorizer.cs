using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Authorization;

namespace Watchtower.Application.Services;

/// <summary>
/// The management surface is the operator population's (docs/central-auth/design.md §13): every handler —
/// and therefore the whole <c>/rpc</c> surface — additionally requires a <em>system-realm</em> principal on
/// top of whatever it declares for itself.
/// </summary>
/// <remarks>
/// Enforced here, in the one seam every transport's authorization decorator goes through, rather than as an
/// attribute on each handler. A rule that has to be repeated per handler is a rule a new handler can be
/// written without, and the handler that forgets it would be the one that hands a customer's account the
/// ability to administer the instance.
/// <para>
/// A realm account's session is perfectly valid — it signs in, it holds a cookie, it passes the forward-auth
/// surface for its own applications, it can call UserInfo. What it cannot do is anything on the management
/// API: creating stacks, editing routes, reading the audit trail. Those are not "operations it lacks a
/// permission for", they are operations about the instance rather than about its population.
/// </para>
/// <para>
/// Three deliberate pass-throughs: an <c>[AllowAnonymous]</c> handler is unaffected (a realm account there
/// is no more than an anonymous visitor, and the login page's own surface has to work before anyone is
/// authenticated at all); an unauthenticated caller is left to the inner authorizer, which already answers
/// <c>Unauthorized</c> and must keep doing so rather than being reported as forbidden; and the implicit
/// local administrator used when <c>Auth:Enabled</c> is false reports the operator realm, so that
/// deployment behaves exactly as it did before realms existed.
/// </para>
/// <para>
/// The management/app APIs (ADR-0008/0009/0011) are token-authenticated and do not pass through here; their
/// principals are stacks rather than accounts and are unchanged in v1. Realm-scoping <em>those</em> is the
/// §13 follow-up — a product's management principal scoped to its own realm, which is what makes
/// cross-population provisioning structurally impossible rather than merely refused.
/// </para>
/// </remarks>
/// <param name="inner">
/// The framework authorizer this decorates, which evaluates the handler's own declared requirements. It runs
/// first so its refusals — and their status codes — are what a caller sees whenever they apply.
/// </param>
/// <param name="currentUser">The principal snapshot the realm claim is read from.</param>
public sealed class SystemRealmAuthorizer(ClaimsAuthorizer inner, ICurrentUser currentUser) : IAuthorizer {
    /// <inheritdoc />
    public async ValueTask<AppError?> AuthorizeAsync(
        AuthorizationRequirements requirements, object? resource, CancellationToken ct) {
        var refusal = await inner.AuthorizeAsync(requirements, resource, ct);
        if (refusal is not null) return refusal;

        if (requirements.AllowAnonymous) return null;
        if (!currentUser.IsAuthenticated) return null;

        // The same deliberately vague message the framework's own forbidden answer uses: which rule refused
        // a call is not something a caller needs to know, and saying so would let one be probed for.
        return WatchtowerClaims.IsSystemRealm(currentUser) ? null : AppError.Forbidden("Access denied.");
    }
}
