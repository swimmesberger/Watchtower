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
    [Fact]
    public async Task AccessibleRouteIds_AgreesWithIsAuthorizedAsync_ForEveryRoute() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await AddUserAsync(host, "alice");

        var routes = new List<Route> {
            await AddRouteAsync(host, "open.example.invalid", AccessMode.Public),
            await AddRouteAsync(host, "internal.example.invalid", AccessMode.Authenticated),
            await AddRouteAsync(host, "granted.example.invalid", AccessMode.Restricted),
            await AddRouteAsync(host, "closed.example.invalid", AccessMode.Restricted),
        };
        await GrantAsync(host, routes[2].Id, userId);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var accessible = await RouteAccessPolicy.AccessibleRouteIdsAsync(db, routes, userId, ct);

        foreach (var route in routes)
            Assert.Equal(
                await RouteAccessPolicy.IsAuthorizedAsync(db, route, userId, ct),
                accessible.Contains(route.Id));
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
}
