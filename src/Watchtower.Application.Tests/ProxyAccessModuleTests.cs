using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the route access-policy handlers (docs/central-auth/design.md §7) through their real generated
/// pipelines, so the <c>[RequireRole("Admin")]</c> decorator and the write-side reconciliation are
/// exercised together: what <see cref="SetAccess"/> persists is what <see cref="GetAccess"/> reads back and
/// what <see cref="RouteAccessPolicy.IsAuthorizedAsync"/> then enforces.
/// </summary>
public sealed class ProxyAccessModuleTests {
    private static readonly Action<IServiceCollection> WithAccessHandlers = services => {
        services.AddGetAccess();
        services.AddSetAccess();
    };

    [Fact]
    public async Task GetAccess_ReflectsWhatSetAccessPersisted_ForARestrictedRoute() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);
        var alice = await SeedUserAsync(host, "alice");
        var bob = await SeedUserAsync(host, "bob");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(
                    routeId, AccessMode.Restricted, "/webhooks/\n/healthz", [alice, bob], IdentityHeaderMode.Remote));
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal(AccessMode.Restricted, result.Value.Mode);
            Assert.Equal(IdentityHeaderMode.Remote, result.Value.IdentityHeaderMode);
            Assert.Equal([alice, bob], result.Value.GrantedUserIds);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<GetAccess.Query, GetAccess.Response>(
                scope.ServiceProvider, new GetAccess.Query(routeId));
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal(AccessMode.Restricted, result.Value.Mode);
            // The identity-header mode round-trips through the store the same way the access mode does.
            Assert.Equal(IdentityHeaderMode.Remote, result.Value.IdentityHeaderMode);
            Assert.Equal("/webhooks/\n/healthz", result.Value.BypassPaths);
            Assert.Equal([alice, bob], result.Value.GrantedUserIds);
        }
    }

    [Fact]
    public async Task SetAccess_PublicToRestricted_PersistsGrants_AndIsAuthorizedHonoursThem() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);
        var alice = await SeedUserAsync(host, "alice");
        var bob = await SeedUserAsync(host, "bob");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Restricted, null, [alice]));
            Assert.True(result.IsSuccess, Describe(result));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
            Assert.Equal(AccessMode.Restricted, route.AccessMode);

            // The read side the verify endpoint uses now lets the granted user in and keeps everyone else out.
            Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, alice, Ct));
            Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, bob, Ct));
        }

        Assert.Equal([AuthEventKinds.RouteAccessChanged], await AuditKindsAsync(host));
    }

    [Fact]
    public async Task SetAccess_SwitchingAwayFromRestricted_ClearsGrantEnforcement() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);
        var alice = await SeedUserAsync(host, "alice");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Restricted, null, [alice]));
            Assert.True(result.IsSuccess, Describe(result));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Authenticated, null, [alice]));
            Assert.True(result.IsSuccess, Describe(result));
            // Grants are meaningless outside Restricted, so the response reports none...
            Assert.Empty(result.Value.GrantedUserIds);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // ...and the rows are gone, so a later switch back to Restricted starts from an empty set.
            Assert.False(await db.RouteAccessGrants.AnyAsync(g => g.RouteId == routeId, Ct));
        }
    }

    [Fact]
    public async Task SetAccess_SwitchingToPublic_ClearsBypassPaths_EvenWhenLinesSupplied() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);

        // Land the route on a protected mode carrying bypass lines...
        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Authenticated, "/webhooks/", []));
            Assert.True(result.IsSuccess, Describe(result));
        }

        // ...then back to Public while still submitting lines — a Public route stores none.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Public, "/webhooks/", []));
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal(AccessMode.Public, result.Value.Mode);
            Assert.Null(result.Value.BypassPaths);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
            Assert.Equal(AccessMode.Public, route.AccessMode);
            Assert.Null(route.BypassPaths);
        }
    }

    [Fact]
    public async Task SetAccess_RejectsABypassLineThatIsNotRooted() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Authenticated, "/ok\nnot-a-path", []));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
            Assert.Contains("not-a-path", result.Error.Message);
        }

        // Nothing was persisted: the route stays at its seeded Public/null policy, and no audit row exists.
        await AssertUnchangedSeededPolicyAsync(host, routeId);
        Assert.Empty(await AuditKindsAsync(host));
    }

    [Fact]
    public async Task SetAccess_RejectsAnUnknownUserId() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);
        var alice = await SeedUserAsync(host, "alice");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Restricted, null, [alice, 4040]));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
            Assert.Contains("4040", result.Error.Message);
        }

        // Nothing was persisted: the route stays at its seeded Public/null policy, no grant, no audit row.
        await AssertUnchangedSeededPolicyAsync(host, routeId);
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.RouteAccessGrants.AnyAsync(g => g.RouteId == routeId, Ct));
        }
        Assert.Empty(await AuditKindsAsync(host));
    }

    [Fact]
    public async Task SetAccess_WithAnOmittedIdentityHeaderMode_DefaultsToNone() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);

        await using (var scope = host.Services.CreateAsyncScope()) {
            // A client that never learned about identity forwarding omits the field entirely; the safe
            // JWT-only default applies rather than the write being rejected — it is optional for this reason.
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Authenticated, null, []));
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal(IdentityHeaderMode.None, result.Value.IdentityHeaderMode);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
            Assert.Equal(IdentityHeaderMode.None, route.IdentityHeaderMode);
        }
    }

    [Fact]
    public async Task SetAccess_RejectsAnUndefinedAccessMode() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, (AccessMode)99, null, []));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        }

        await AssertUnchangedSeededPolicyAsync(host, routeId);
        Assert.Empty(await AuditKindsAsync(host));
    }

    [Fact]
    public async Task SetAccess_RejectsAnUndefinedIdentityHeaderMode() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                // A value outside the enum must not be persisted and later read back as something the
                // forwarding helper cannot map.
                new SetAccess.Command(routeId, AccessMode.Authenticated, null, [], (IdentityHeaderMode)99));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        }

        await AssertUnchangedSeededPolicyAsync(host, routeId);
        Assert.Empty(await AuditKindsAsync(host));
    }

    [Fact]
    public async Task SetAccess_ReconcilesGrants_WithoutDuplicatingRows() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteAsync(host);
        var alice = await SeedUserAsync(host, "alice");
        var bob = await SeedUserAsync(host, "bob");
        var carol = await SeedUserAsync(host, "carol");

        // Save the same set twice, then shift it: alice stays, bob leaves, carol joins.
        await SetGrantsAsync(host, routeId, [alice, bob]);
        await SetGrantsAsync(host, routeId, [alice, bob]);
        await SetGrantsAsync(host, routeId, [alice, carol]);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var grants = await db.RouteAccessGrants.AsNoTracking()
            .Where(g => g.RouteId == routeId)
            .Select(g => g.UserId)
            .OrderBy(id => id)
            .ToListAsync(Ct);

        // Exactly one row per current member — no duplicate for the user who was present across saves.
        Assert.Equal([alice, carol], grants);
    }

    [Fact]
    public async Task SetAccess_ReportsAnUnknownRouteAsNotFound() {
        using var host = AuthTestHost.Start(WithAccessHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
            scope.ServiceProvider,
            new SetAccess.Command(404, AccessMode.Authenticated, null, []));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    public async Task AccessHandlers_WithAuthEnabled_AreDeniedToANonAdministrator(string operation) {
        using var host = AuthTestHost.Start(WithAccessHandlers, ("Watchtower:Auth:Enabled", "true"));
        var routeId = await SeedRouteAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        SeedPrincipal(sp, isAdmin: false);

        var kind = operation switch {
            "get" => await DeniedKindAsync<GetAccess.Query, GetAccess.Response>(
                sp, new GetAccess.Query(routeId)),
            "set" => await DeniedKindAsync<SetAccess.Command, SetAccess.Response>(
                sp, new SetAccess.Command(routeId, AccessMode.Restricted, null, [])),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        // Refused by the decorator before the handler runs, so nothing is persisted or audited.
        Assert.Equal(ErrorKind.Forbidden, kind);
        Assert.Empty(await AuditKindsAsync(host));
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>()
            .HandleAsync(request, Ct);

    private static async ValueTask<ErrorKind> DeniedKindAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) {
        var result = await SendAsync<TRequest, TResponse>(scope, request);
        Assert.False(result.IsSuccess);
        return result.Error.Kind;
    }

    private static async Task SetGrantsAsync(AuthTestHost host, int routeId, IReadOnlyList<int> userIds) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
            scope.ServiceProvider,
            new SetAccess.Command(routeId, AccessMode.Restricted, null, userIds));
        Assert.True(result.IsSuccess, Describe(result));
    }

    /// <summary>Seeds a stack and a Public route on it, returning the route id.</summary>
    private static async Task<int> SeedRouteAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = "demo",
            RepositoryUrl = "https://example.invalid/demo.git",
            ComposeFilePath = "docker-compose.yml",
            Branch = "main",
            ComposeProjectName = "demo",
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);

        var route = new Route {
            StackId = stack.Id,
            Domain = "demo.example.invalid",
            ServiceName = "web",
            ContainerPort = 8080,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(Ct);
        return route.Id;
    }

    private static async Task<int> SeedUserAsync(AuthTestHost host, string userName) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<User>>();
        var user = AuthTestHost.NewUser(userName);
        var created = await users.CreateAsync(user, "correct-horse-battery");
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user.Id;
    }

    /// <summary>Reloads the route and asserts its policy is still exactly what <see cref="SeedRouteAsync"/> left.</summary>
    private static async Task AssertUnchangedSeededPolicyAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
        Assert.Equal(AccessMode.Public, route.AccessMode);
        Assert.Null(route.BypassPaths);
    }

    private static async Task<IReadOnlyList<string>> AuditKindsAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuthEvents.OrderBy(e => e.Id).Select(e => e.Kind).ToListAsync(Ct);
    }

    /// <summary>Applies a principal the way every Elarion transport does — through the dispatch-scope rail.</summary>
    private static void SeedPrincipal(IServiceProvider scope, bool isAdmin) {
        var claims = new List<Claim> {
            new(WatchtowerClaims.UserId, "7"),
            new(WatchtowerClaims.Name, "caller"),
        };
        if (isAdmin) claims.Add(new Claim(WatchtowerClaims.Role, WatchtowerClaims.AdminRole));

        var context = new DispatchScopeContext();
        context.Set(new ClaimsPrincipal(new ClaimsIdentity(
            claims, "WatchtowerSession", WatchtowerClaims.Name, WatchtowerClaims.Role)));
        scope.SeedScope(context);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
