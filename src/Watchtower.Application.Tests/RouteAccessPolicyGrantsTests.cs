using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the grant-backed half of <see cref="RouteAccessPolicy"/>: <c>IsAuthorizedAsync</c> for one route
/// and <c>AccessibleRouteIdsAsync</c> for a whole set of them. The set form is what the tenant-discovery
/// endpoints answer with, so what matters here is that it says exactly what asking route by route would —
/// and that both stay fail-closed on a mode this build does not know.
/// </summary>
public sealed class RouteAccessPolicyGrantsTests {
    [Fact]
    public async Task AccessibleRouteIds_AllowsPublicAndAuthenticated_AndRestrictedOnlyWithAGrant() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await host.AddUserAsync("alice");

        var open = await host.AddRouteAsync("open.example.invalid", AccessMode.Public);
        var signedIn = await host.AddRouteAsync("internal.example.invalid", AccessMode.Authenticated);
        var granted = await host.AddRouteAsync("granted.example.invalid", AccessMode.Restricted);
        var ungranted = await host.AddRouteAsync("closed.example.invalid", AccessMode.Restricted);
        await host.GrantUserAsync(granted.Id, userId);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var accessible = await RouteAccessPolicy.AccessibleRouteIdsAsync(
            db, [open, signedIn, granted, ungranted], userId, ct);

        Assert.Equal([open.Id, signedIn.Id, granted.Id], accessible.Order());
    }

    /// <summary>
    /// The anti-drift assertion. The two entry points are separate methods running separate queries, and a
    /// disagreement between them would not merely be a wrong answer somewhere — it would be one surface
    /// showing a user an app another surface refuses them. So the set is compared against the per-route
    /// verdict, route by route.
    /// </summary>
    /// <remarks>
    /// The estate deliberately covers every shape a grant can take, because the two methods now fold a
    /// membership subquery into their grant lookup and that is exactly the kind of change that can make one
    /// of them answer differently: a direct grant, a grant to a group the account is in, a grant to a group
    /// it is not in, and a route holding both kinds at once.
    /// </remarks>
    [Fact]
    public async Task AccessibleRouteIds_AgreesWithIsAuthorizedAsync_ForEveryRoute() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await host.AddUserAsync("alice");
        var stranger = await host.AddUserAsync("bob");
        var mine = await host.AddGroupAsync("staff", userId);
        var theirs = await host.AddGroupAsync("others", stranger);

        var routes = new List<Route> {
            await host.AddRouteAsync("open.example.invalid", AccessMode.Public),
            await host.AddRouteAsync("internal.example.invalid", AccessMode.Authenticated),
            await host.AddRouteAsync("granted.example.invalid", AccessMode.Restricted),
            await host.AddRouteAsync("closed.example.invalid", AccessMode.Restricted),
            await host.AddRouteAsync("viagroup.example.invalid", AccessMode.Restricted),
            await host.AddRouteAsync("othergroup.example.invalid", AccessMode.Restricted),
            await host.AddRouteAsync("both.example.invalid", AccessMode.Restricted),
        };
        await host.GrantUserAsync(routes[2].Id, userId);
        await host.GrantGroupAsync(routes[4].Id, mine);
        await host.GrantGroupAsync(routes[5].Id, theirs);
        await host.GrantUserAsync(routes[6].Id, userId);
        await host.GrantGroupAsync(routes[6].Id, mine);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var accessible = await RouteAccessPolicy.AccessibleRouteIdsAsync(db, routes, userId, ct);

        foreach (var route in routes)
            Assert.Equal(
                await RouteAccessPolicy.IsAuthorizedAsync(db, route, userId, ct),
                accessible.Contains(route.Id));

        // ...and the verdicts themselves are the expected ones, so the two agreeing on the wrong answer
        // would not pass either.
        Assert.Equal(
            [routes[0].Id, routes[1].Id, routes[2].Id, routes[4].Id, routes[6].Id],
            accessible.Order());
    }

    /// <summary>
    /// A group grant is access, and losing the membership is losing the access — with no cache in between,
    /// so it takes effect on the next question asked rather than at some later refresh.
    /// </summary>
    [Fact]
    public async Task AGroupGrant_AdmitsMembersOnly_AndIsRevokedByLeavingTheGroup() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        var groupId = await host.AddGroupAsync("staff", alice);

        var route = await host.AddRouteAsync("granted.example.invalid", AccessMode.Restricted);
        await host.GrantGroupAsync(route.Id, groupId);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, alice, ct));
            Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, bob, ct));
            Assert.Equal([route.Id], await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [route], alice, ct));
            Assert.Empty(await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [route], bob, ct));
        }

        await host.RemoveFromGroupAsync(groupId, alice);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, alice, ct));
            Assert.Empty(await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [route], alice, ct));
        }
    }

    /// <summary>
    /// Deleting the group revokes what it granted, and does so through the foreign-key cascade rather than
    /// by anything the policy evaluator does — which is why it is asserted here rather than only in the
    /// module tests.
    /// </summary>
    [Fact]
    public async Task DeletingTheGrantedGroup_RevokesTheAccessItCarried() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var alice = await host.AddUserAsync("alice");
        var groupId = await host.AddGroupAsync("staff", alice);

        var route = await host.AddRouteAsync("granted.example.invalid", AccessMode.Restricted);
        await host.GrantGroupAsync(route.Id, groupId);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var group = await db.Groups.SingleAsync(g => g.Id == groupId, ct);
            db.Groups.Remove(group);
            await db.SaveChangesAsync(ct);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.RouteAccessGrants.AnyAsync(g => g.RouteId == route.Id, ct));
            Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, alice, ct));
        }
    }

    /// <summary>
    /// A direct grant and a group grant on the same route are two independent reasons to be let in, so
    /// withdrawing one leaves the other standing. Getting this wrong in either direction is a hole or a
    /// lockout, and the two partial unique indexes are what let both rows coexist in the first place.
    /// </summary>
    [Fact]
    public async Task DirectAndGroupGrantsOnOneRoute_AreIndependentReasonsToBeAdmitted() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var alice = await host.AddUserAsync("alice");
        var groupId = await host.AddGroupAsync("staff", alice);

        var route = await host.AddRouteAsync("granted.example.invalid", AccessMode.Restricted);
        await host.GrantUserAsync(route.Id, alice);
        await host.GrantGroupAsync(route.Id, groupId);

        // Drop the direct grant: the group still admits her.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var direct = await db.RouteAccessGrants.SingleAsync(g => g.RouteId == route.Id && g.UserId != null, ct);
            db.RouteAccessGrants.Remove(direct);
            await db.SaveChangesAsync(ct);

            Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, alice, ct));
        }

        // Drop the membership too, and nothing is left.
        await host.RemoveFromGroupAsync(groupId, alice);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, alice, ct));
        }
    }

    /// <summary>
    /// A group grant is not a licence to reinterpret an unknown mode either: the fail-closed reading of
    /// <c>Classify</c> comes first, before any subject is considered.
    /// </summary>
    [Fact]
    public async Task AnUnknownAccessMode_FailsClosed_EvenWithAGroupGrantOnThatRoute() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var alice = await host.AddUserAsync("alice");
        var groupId = await host.AddGroupAsync("staff", alice);

        var stored = await host.AddRouteAsync("granted.example.invalid", AccessMode.Restricted);
        await host.GrantGroupAsync(stored.Id, groupId);
        var future = new Route {
            Id = stored.Id,
            StackId = stored.StackId,
            Domain = stored.Domain,
            ServiceName = "web",
            ContainerPort = 8080,
            AccessMode = (AccessMode)99,
        };

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, stored, alice, ct));
        Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, future, alice, ct));
        Assert.Empty(await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [future], alice, ct));
    }

    /// <summary>
    /// A mode from a future build, carried on a route the user <em>does</em> hold a grant for: were an
    /// unrecognised mode ever read as "restricted" rather than refused outright, that grant would let it
    /// through. Both entry points must answer no.
    /// </summary>
    [Fact]
    public async Task AnUnknownAccessMode_FailsClosed_EvenWithAGrantOnThatRoute() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await host.AddUserAsync("alice");

        var stored = await host.AddRouteAsync("granted.example.invalid", AccessMode.Restricted);
        await host.GrantUserAsync(stored.Id, userId);
        var future = new Route {
            Id = stored.Id,
            StackId = stored.StackId,
            Domain = stored.Domain,
            ServiceName = "web",
            ContainerPort = 8080,
            AccessMode = (AccessMode)99,
        };

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        // The grant is genuinely there — the stored route with the same id is accessible.
        Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, stored, userId, ct));
        Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, future, userId, ct));
        Assert.Empty(await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [future], userId, ct));
    }

    [Fact]
    public async Task AccessibleRouteIds_OnlyHonoursTheGrantsOfTheUserBeingAsked() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");

        var route = await host.AddRouteAsync("granted.example.invalid", AccessMode.Restricted);
        await host.GrantUserAsync(route.Id, alice);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        Assert.Equal([route.Id], await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [route], alice, ct));
        Assert.Empty(await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [route], bob, ct));
    }

    [Fact]
    public async Task AccessibleRouteIds_OfNothingIsEmpty() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        Assert.Empty(await RouteAccessPolicy.AccessibleRouteIdsAsync(
            db, [], userId: 1, TestContext.Current.CancellationToken));
    }
}
