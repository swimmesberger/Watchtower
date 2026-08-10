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
        var userId = await AddUserAsync(host, "alice");

        var open = await AddRouteAsync(host, "open.example.invalid", AccessMode.Public);
        var signedIn = await AddRouteAsync(host, "internal.example.invalid", AccessMode.Authenticated);
        var granted = await AddRouteAsync(host, "granted.example.invalid", AccessMode.Restricted);
        var ungranted = await AddRouteAsync(host, "closed.example.invalid", AccessMode.Restricted);
        await GrantAsync(host, granted.Id, userId);

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
        var userId = await AddUserAsync(host, "alice");
        var stranger = await AddUserAsync(host, "bob");
        var mine = await AddGroupAsync(host, "staff", userId);
        var theirs = await AddGroupAsync(host, "others", stranger);

        var routes = new List<Route> {
            await AddRouteAsync(host, "open.example.invalid", AccessMode.Public),
            await AddRouteAsync(host, "internal.example.invalid", AccessMode.Authenticated),
            await AddRouteAsync(host, "granted.example.invalid", AccessMode.Restricted),
            await AddRouteAsync(host, "closed.example.invalid", AccessMode.Restricted),
            await AddRouteAsync(host, "viagroup.example.invalid", AccessMode.Restricted),
            await AddRouteAsync(host, "othergroup.example.invalid", AccessMode.Restricted),
            await AddRouteAsync(host, "both.example.invalid", AccessMode.Restricted),
        };
        await GrantAsync(host, routes[2].Id, userId);
        await GrantGroupAsync(host, routes[4].Id, mine);
        await GrantGroupAsync(host, routes[5].Id, theirs);
        await GrantAsync(host, routes[6].Id, userId);
        await GrantGroupAsync(host, routes[6].Id, mine);

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
        var alice = await AddUserAsync(host, "alice");
        var bob = await AddUserAsync(host, "bob");
        var groupId = await AddGroupAsync(host, "staff", alice);

        var route = await AddRouteAsync(host, "granted.example.invalid", AccessMode.Restricted);
        await GrantGroupAsync(host, route.Id, groupId);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, alice, ct));
            Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, bob, ct));
            Assert.Equal([route.Id], await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [route], alice, ct));
            Assert.Empty(await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [route], bob, ct));
        }

        await RemoveMembershipAsync(host, groupId, alice);

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
        var alice = await AddUserAsync(host, "alice");
        var groupId = await AddGroupAsync(host, "staff", alice);

        var route = await AddRouteAsync(host, "granted.example.invalid", AccessMode.Restricted);
        await GrantGroupAsync(host, route.Id, groupId);

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
        var alice = await AddUserAsync(host, "alice");
        var groupId = await AddGroupAsync(host, "staff", alice);

        var route = await AddRouteAsync(host, "granted.example.invalid", AccessMode.Restricted);
        await GrantAsync(host, route.Id, alice);
        await GrantGroupAsync(host, route.Id, groupId);

        // Drop the direct grant: the group still admits her.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var direct = await db.RouteAccessGrants.SingleAsync(g => g.RouteId == route.Id && g.UserId != null, ct);
            db.RouteAccessGrants.Remove(direct);
            await db.SaveChangesAsync(ct);

            Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, alice, ct));
        }

        // Drop the membership too, and nothing is left.
        await RemoveMembershipAsync(host, groupId, alice);

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
        var alice = await AddUserAsync(host, "alice");
        var groupId = await AddGroupAsync(host, "staff", alice);

        var stored = await AddRouteAsync(host, "granted.example.invalid", AccessMode.Restricted);
        await GrantGroupAsync(host, stored.Id, groupId);
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
        var userId = await AddUserAsync(host, "alice");

        var stored = await AddRouteAsync(host, "granted.example.invalid", AccessMode.Restricted);
        await GrantAsync(host, stored.Id, userId);
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
        var alice = await AddUserAsync(host, "alice");
        var bob = await AddUserAsync(host, "bob");

        var route = await AddRouteAsync(host, "granted.example.invalid", AccessMode.Restricted);
        await GrantAsync(host, route.Id, alice);

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

    // ── Seeding ────────────────────────────────────────────────────────────────

    private static async Task<Route> AddRouteAsync(AuthTestHost host, string domain, AccessMode mode) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var name = domain.Split('.')[0];
        var stack = new Stack {
            Name = name,
            RepositoryUrl = $"https://example.invalid/{name}.git",
            ComposeFilePath = "docker-compose.yml",
            Branch = "main",
            ComposeProjectName = name,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(ct);

        var route = new Route {
            StackId = stack.Id,
            Domain = domain,
            ServiceName = "web",
            ContainerPort = 8080,
            AccessMode = mode,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(ct);
        return route;
    }

    private static async Task<int> AddUserAsync(AuthTestHost host, string userName) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = AuthTestHost.NewUser(userName);
        var created = await users.CreateAsync(user, "correct-horse-battery");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task GrantAsync(AuthTestHost host, int routeId, int userId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.RouteAccessGrants.Add(new RouteAccessGrant { RouteId = routeId, UserId = userId });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task GrantGroupAsync(AuthTestHost host, int routeId, int groupId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.RouteAccessGrants.Add(new RouteAccessGrant { RouteId = routeId, GroupId = groupId });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Creates a group holding <paramref name="memberIds"/> and returns its id.</summary>
    private static async Task<int> AddGroupAsync(AuthTestHost host, string name, params int[] memberIds) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var group = new Group { Name = name, NormalizedName = name.ToUpperInvariant() };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);

        foreach (var userId in memberIds)
            db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync(ct);
        return group.Id;
    }

    private static async Task RemoveMembershipAsync(AuthTestHost host, int groupId, int userId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var member = await db.GroupMembers.SingleAsync(m => m.GroupId == groupId && m.UserId == userId, ct);
        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync(ct);
    }
}
