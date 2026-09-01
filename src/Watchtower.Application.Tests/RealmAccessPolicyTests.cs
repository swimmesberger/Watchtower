using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

// Watchtower's own entity, not Microsoft.AspNetCore.Routing.Route.
using Route = Watchtower.Application.Entities.Route;

namespace Watchtower.Application.Tests;

/// <summary>
/// The realm invariant on <see cref="RouteAccessPolicy"/> (docs/central-auth/design.md §13): a protected
/// route only ever admits accounts of its own realm, whatever its grants say — and both entry points must
/// say so, because one of them showing a user an app the other refuses would be the hole the file's own
/// remarks warn about.
/// </summary>
public sealed class RealmAccessPolicyTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AuthenticatedRoute_RefusesAnAccountFromAnotherRealm() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        var route = await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Authenticated, template);

        var insider = await host.AddUserAsync("carol", realmId: acme);
        var outsider = await host.AddUserAsync("alice");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, insider, Ct));
        // "Any authenticated user" has always meant "any user of this population"; before realms there was
        // only one, and the invariant is what keeps that reading true now that there can be several.
        Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, outsider, Ct));
    }

    [Fact]
    public async Task RestrictedRoute_RefusesAWrongRealmSubject_EvenWithAGrant() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        var route = await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Restricted, template);

        var outsider = await host.AddUserAsync("alice");
        // Written directly, the way a grant left behind by an earlier configuration would be: proxy.setAccess
        // refuses to create one, and this is the second line of defence behind that.
        await host.GrantUserAsync(route.Id, outsider);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, outsider, Ct));
    }

    [Fact]
    public async Task RestrictedRoute_RefusesAWrongRealmGroupGrantToo() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        var route = await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Restricted, template);

        var outsider = await host.AddUserAsync("alice");
        var operators = await host.AddGroupAsync("staff", outsider);
        await host.GrantGroupAsync(route.Id, operators);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        // Membership resolves inside the grant query, so this is the same predicate reached by another
        // path — the realm check has to sit in front of both.
        Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, outsider, Ct));
    }

    [Fact]
    public async Task PublicRoute_IsUnaffectedByRealms() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        var route = await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Public, template);
        var outsider = await host.AddUserAsync("alice");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        // A public route asks nobody who they are, so there is no population to compare it against.
        Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, outsider, Ct));
    }

    /// <summary>
    /// A <see cref="RouteTarget.Watchtower"/> route has no stack to inherit a realm from and states its
    /// own (ADR-0023), which is what makes <c>ResolveByHostAsync</c> and the grant editor agree about the
    /// population a hostname belongs to.
    /// </summary>
    [Fact]
    public async Task AWatchtowerRoute_BelongsToTheRealmItNames() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var realmRoute = await host.AddWatchtowerRouteAsync("portal.acme.invalid", acme);
        var operatorRoute = await host.AddWatchtowerRouteAsync("ui.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var realms = await RouteAccessPolicy.RouteRealmIdsAsync(
            db, [realmRoute.Id, operatorRoute.Id], Ct);
        Assert.Equal(acme, realms[realmRoute.Id]);
        Assert.Equal(Realm.SystemRealmId, realms[operatorRoute.Id]);
    }

    [Fact]
    public async Task StandaloneStacks_BelongToTheOperatorRealm() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var route = await host.AddRouteAsync("solo.example.invalid", AccessMode.Authenticated);

        var operatorUser = await host.AddUserAsync("alice");
        var realmUser = await host.AddUserAsync("carol", realmId: acme);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, operatorUser, Ct));
        Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, realmUser, Ct));
    }

    /// <summary>
    /// The anti-drift assertion, extended across realms: the bulk answer and the per-route answer are
    /// separate queries, and the realm predicate had to be added to both. An estate spanning two realms and
    /// every access mode is compared route by route.
    /// </summary>
    [Fact]
    public async Task AccessibleRouteIds_AgreesWithIsAuthorizedAsync_AcrossRealms() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var acmeTemplate = await host.AddRealmTemplateAsync("shop", acme);
        var operatorTemplate = await host.AddRealmTemplateAsync("tools");

        var carol = await host.AddUserAsync("carol", realmId: acme);
        var alice = await host.AddUserAsync("alice");

        var routes = new List<Route> {
            await host.AddRouteAsync("open.example.invalid", AccessMode.Public),
            await host.AddRouteAsync("solo.example.invalid", AccessMode.Authenticated),
            await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Authenticated, acmeTemplate),
            await host.AddRouteAsync("two.shop.example.invalid", AccessMode.Restricted, acmeTemplate),
            await host.AddRouteAsync("three.shop.example.invalid", AccessMode.Restricted, acmeTemplate),
            await host.AddRouteAsync("alpha.tools.example.invalid", AccessMode.Restricted, operatorTemplate),
        };
        await host.GrantUserAsync(routes[3].Id, carol);
        // Cross-realm grants, both directions: neither may admit anyone.
        await host.GrantUserAsync(routes[4].Id, alice);
        await host.GrantUserAsync(routes[5].Id, carol);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        foreach (var userId in new[] { carol, alice }) {
            var bulk = await RouteAccessPolicy.AccessibleRouteIdsAsync(db, routes, userId, Ct);
            foreach (var route in routes) {
                var single = await RouteAccessPolicy.IsAuthorizedAsync(db, route, userId, Ct);
                Assert.Equal(single, bulk.Contains(route.Id));
            }
        }

        // And the answers themselves, so the agreement above is agreement on the right thing.
        var forCarol = await RouteAccessPolicy.AccessibleRouteIdsAsync(db, routes, carol, Ct);
        Assert.Equal([routes[0].Id, routes[2].Id, routes[3].Id], forCarol.Order());

        var forAlice = await RouteAccessPolicy.AccessibleRouteIdsAsync(db, routes, alice, Ct);
        Assert.Equal([routes[0].Id, routes[1].Id], forAlice.Order());
    }

    [Fact]
    public async Task AnAccountThatNoLongerExists_ReachesNothingProtected() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("solo.example.invalid", AccessMode.Authenticated);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        // "No realm" never equals a route's realm — the invariant is fail-closed on a missing side, not
        // permissive.
        Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, 4040, Ct));
        Assert.Empty(await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [route], 4040, Ct));
    }

    // -- Tenant discovery ------------------------------------------------------------------------

    /// <summary>
    /// The discovery endpoints know exactly which realm they are answering for — the calling stack's — so
    /// they pin the accepted issuer to that one realm rather than accepting any of ours.
    /// </summary>
    [Fact]
    public async Task TenantDiscovery_OnlyAcceptsAnAssertionFromTheCallingStacksRealm() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", "watchtower.example.invalid"));
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        var route = await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Authenticated, template);
        var carol = await host.AddUserAsync("carol", realmId: acme);

        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<WatchtowerDbContext>();
        var signer = sp.GetRequiredService<AuthTokenSigner>();
        var discovery = sp.GetRequiredService<TenantDiscoveryService>();
        var user = await db.Users.SingleAsync(u => u.Id == carol, Ct);
        var stackId = (await db.Routes.SingleAsync(r => r.Id == route.Id, Ct)).StackId!.Value;

        var realms = sp.GetRequiredService<RealmResolver>();
        var acmeRealm = await db.Realms.SingleAsync(r => r.Id == acme, Ct);
        Assert.NotNull(route.Domain);
        var mintedForAcme = signer.Mint(user, route.Domain, await realms.IdentityForAsync(acmeRealm, Ct));
        Assert.Equal(carol, await discovery.ResolveAssertionSubjectAsync(stackId, mintedForAcme, Ct));

        // Same key, same audience, same subject — only the issuer says another population. Accepting it
        // would mean any realm's assertion could answer a question about this one.
        var mintedForOperator = signer.Mint(user, route.Domain, RealmIdentity.System);
        Assert.Null(await discovery.ResolveAssertionSubjectAsync(stackId, mintedForOperator, Ct));
    }

    [Fact]
    public async Task TenantDiscovery_RefusesASubjectFromAnotherRealm() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", "watchtower.example.invalid"));
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        var route = await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Authenticated, template);
        var alice = await host.AddUserAsync("alice");

        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<WatchtowerDbContext>();
        var signer = sp.GetRequiredService<AuthTokenSigner>();
        var discovery = sp.GetRequiredService<TenantDiscoveryService>();
        var user = await db.Users.SingleAsync(u => u.Id == alice, Ct);
        var stackId = (await db.Routes.SingleAsync(r => r.Id == route.Id, Ct)).StackId!.Value;
        var realms = sp.GetRequiredService<RealmResolver>();
        var acmeRealm = await db.Realms.SingleAsync(r => r.Id == acme, Ct);

        // Defence in depth: the issuer is the calling realm's, but the account named by `sub` is not in it.
        // One key pair signs every realm, so the subject has to be re-checked, not inferred from the issuer.
        Assert.NotNull(route.Domain);
        var token = signer.Mint(user, route.Domain, await realms.IdentityForAsync(acmeRealm, Ct));
        Assert.Null(await discovery.ResolveAssertionSubjectAsync(stackId, token, Ct));
    }
}
