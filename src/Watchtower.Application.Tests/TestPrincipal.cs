using System.Security.Claims;
using Elarion.Abstractions.Dispatch;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;

namespace Watchtower.Application.Tests;

/// <summary>
/// Builds and applies the principal a signed-in caller dispatches with, so the module tests all describe
/// the same thing the running host produces.
/// </summary>
/// <remarks>
/// One helper rather than a copy per test class, because a principal now has to state its realm: the
/// management surface is gated on that claim (<see cref="SystemRealmAuthorizer"/>), and a per-file
/// rebuild of the claim set would be five places to forget it — each of which would quietly turn every
/// handler test in that file into a test of the gate.
/// </remarks>
internal static class TestPrincipal {
    /// <summary>
    /// The principal <c>WatchtowerSessionAuthenticationHandler</c> mints, rebuilt from the same
    /// <see cref="WatchtowerClaims"/> constants — which is the point of those constants existing.
    /// </summary>
    public static ClaimsPrincipal New(
        string id = "7",
        string name = "caller",
        bool isAdmin = true,
        string? email = null,
        string realmSlug = Realm.SystemRealmSlug) {
        var claims = new List<Claim> {
            new(WatchtowerClaims.UserId, id),
            new(WatchtowerClaims.Name, name),
            new(WatchtowerClaims.RealmSlug, realmSlug),
        };
        if (email is not null) claims.Add(new Claim(WatchtowerClaims.Email, email));
        if (isAdmin) claims.Add(new Claim(WatchtowerClaims.Role, WatchtowerClaims.AdminRole));

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims, "WatchtowerSession", WatchtowerClaims.Name, WatchtowerClaims.Role));
    }

    /// <summary>Applies a principal the way every Elarion transport does — through the dispatch-scope rail.</summary>
    public static void Seed(IServiceProvider scope, ClaimsPrincipal principal) {
        var context = new DispatchScopeContext();
        context.Set(principal);
        scope.SeedScope(context);
    }

    /// <summary>Shorthand for the common case: a caller in the operator realm, admin or not.</summary>
    public static void Seed(IServiceProvider scope, bool isAdmin) => Seed(scope, New(isAdmin: isAdmin));
}
