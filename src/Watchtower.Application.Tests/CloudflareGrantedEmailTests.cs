using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// What a <see cref="AccessMode.Restricted"/> route's grants resolve to at the Cloudflare edge — the emails
/// its Access application admits (ADR-0040 decision 3).
/// </summary>
/// <remarks>
/// Asserted here rather than through the projection because the realm rule is invisible in the projection's
/// output: a grant filtered out for crossing a realm boundary and a grant that was never written produce
/// exactly the same empty allow-list. The difference only shows at the point the addresses are resolved,
/// which is what these tests pin.
/// <para>
/// The invariant itself is <c>RouteAccessPolicy</c>'s — <b>a protected route is only ever reachable by an
/// account of its own realm, whatever its grants say</b> (design.md §13.5). It used to hold in process and
/// not at the edge, so one grant table admitted differently depending on which provider was serving.
/// </para>
/// </remarks>
public sealed class CloudflareGrantedEmailTests {
    [Fact]
    public async Task ADirectGrant_ContributesTheAccountsAddress() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var alice = await host.AddUserAsync("alice");
        await host.SetEmailAsync(alice, "alice@example.com");
        await host.GrantUserAsync(route.Id, alice);

        var granted = await ResolveAsync(host, route.Id);

        Assert.Equal(["alice@example.com"], granted[route.Id]);
    }

    [Fact]
    public async Task AGroupGrant_ContributesEveryMembersAddress() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        await host.SetEmailAsync(alice, "alice@example.com");
        await host.SetEmailAsync(bob, "bob@example.com");
        var groupId = await host.AddGroupAsync("staff", alice, bob);
        await host.GrantGroupAsync(route.Id, groupId);

        var granted = await ResolveAsync(host, route.Id);

        Assert.Equal(["alice@example.com", "bob@example.com"], granted[route.Id].Order());
    }

    [Fact]
    public async Task AnAccountWithoutAnAddress_ContributesNothing() {
        // Cloudflare matches on email, so an account without one cannot be admitted there at all. Worth
        // pinning because it is the one exclusion that is *not* a policy decision.
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var alice = await host.AddUserAsync("alice");
        await host.GrantUserAsync(route.Id, alice);

        var granted = await ResolveAsync(host, route.Id);

        Assert.False(granted.ContainsKey(route.Id));
    }

    [Fact]
    public async Task ADisabledAccount_ContributesNothing() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var alice = await host.AddUserAsync("alice");
        await host.SetEmailAsync(alice, "alice@example.com");
        await host.DisableUserAsync(alice);
        await host.GrantUserAsync(route.Id, alice);

        var granted = await ResolveAsync(host, route.Id);

        Assert.False(granted.ContainsKey(route.Id));
    }

    [Fact]
    public async Task AGrantLeftBehindByARealmChange_ContributesNothing() {
        // The regression this fix exists for. The grant is legitimate when written — proxy.setAccess refuses
        // one across realms — and becomes stale when the route's stack moves to a category of another realm,
        // taking the route's population with it. In process such a grant "grants nothing rather than crossing
        // the boundary"; at the edge it used to admit whoever presented the address.
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var alice = await host.AddUserAsync("alice");
        await host.SetEmailAsync(alice, "alice@example.com");
        await host.GrantUserAsync(route.Id, alice);
        // Sanity: the grant admits while both sides are the operator realm's.
        Assert.Equal(["alice@example.com"], (await ResolveAsync(host, route.Id))[route.Id]);

        await host.MoveRouteToRealmAsync(route.Id, await host.AddRealmAsync("tenants"));

        var granted = await ResolveAsync(host, route.Id);

        Assert.False(granted.ContainsKey(route.Id));
    }

    [Fact]
    public async Task AGroupMemberLeftBehindByARealmChange_ContributesNothing() {
        // The same invariant one hop out: a group grant must not be the way around it.
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var alice = await host.AddUserAsync("alice");
        await host.SetEmailAsync(alice, "alice@example.com");
        var groupId = await host.AddGroupAsync("staff", alice);
        await host.GrantGroupAsync(route.Id, groupId);

        await host.MoveRouteToRealmAsync(route.Id, await host.AddRealmAsync("tenants"));

        var granted = await ResolveAsync(host, route.Id);

        Assert.False(granted.ContainsKey(route.Id));
    }

    private static async Task<Dictionary<int, string[]>> ResolveAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await CloudflareTunnelProvider.GrantedEmailsAsync(
            db, [routeId], TestContext.Current.CancellationToken);
    }
}
