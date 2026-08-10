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
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");

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
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");

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

        Assert.Equal([AuthEventKinds.RouteAccessChanged], await host.AuditKindsAsync());
    }

    [Fact]
    public async Task SetAccess_SwitchingAwayFromRestricted_ClearsGrantEnforcement() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");

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
        var routeId = await SeedRouteIdAsync(host);

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
        var routeId = await SeedRouteIdAsync(host);

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
        Assert.Empty(await host.AuditKindsAsync());
    }

    [Fact]
    public async Task SetAccess_RejectsAnUnknownUserId() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");

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
        Assert.Empty(await host.AuditKindsAsync());
    }

    [Fact]
    public async Task SetAccess_WithAnOmittedIdentityHeaderMode_DefaultsToNone() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);

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
        var routeId = await SeedRouteIdAsync(host);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, (AccessMode)99, null, []));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        }

        await AssertUnchangedSeededPolicyAsync(host, routeId);
        Assert.Empty(await host.AuditKindsAsync());
    }

    [Fact]
    public async Task SetAccess_RejectsAnUndefinedIdentityHeaderMode() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);

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
        Assert.Empty(await host.AuditKindsAsync());
    }

    [Fact]
    public async Task SetAccess_ReconcilesGrants_WithoutDuplicatingRows() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        var carol = await host.AddUserAsync("carol");

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

    // -- Group grants ----------------------------------------------------------------------------

    [Fact]
    public async Task SetAccess_RoundTripsGroupGrants_AndIsAuthorizedHonoursMembership() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        var carol = await host.AddUserAsync("carol");
        var staff = await host.AddGroupAsync("staff", alice);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Restricted, null, [bob], null, [staff]));

            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal([bob], result.Value.GrantedUserIds);
            Assert.Equal([staff], result.Value.GrantedGroupIds);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<GetAccess.Query, GetAccess.Response>(
                scope.ServiceProvider, new GetAccess.Query(routeId));
            Assert.True(result.IsSuccess, Describe(result));
            // The two subject kinds come back separately, so a form can restore exactly what it submitted.
            Assert.Equal([bob], result.Value.GrantedUserIds);
            Assert.Equal([staff], result.Value.GrantedGroupIds);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
            // alice holds no grant of her own; the group is what lets her in. carol has neither.
            Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, alice, Ct));
            Assert.True(await RouteAccessPolicy.IsAuthorizedAsync(db, route, bob, Ct));
            Assert.False(await RouteAccessPolicy.IsAuthorizedAsync(db, route, carol, Ct));
        }
    }

    [Fact]
    public async Task SetAccess_ReconcilesGroupGrantsIndependentlyOfUserGrants() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");
        var staff = await host.AddGroupAsync("staff", alice);
        var viewers = await host.AddGroupAsync("viewers");

        await SetAccessAsync(host, routeId, [alice], [staff]);
        // Re-saving the same policy twice must not churn rows, and shifting one axis must not disturb the
        // other: the user grant stays put while the group grant is swapped.
        await SetAccessAsync(host, routeId, [alice], [staff]);
        await SetAccessAsync(host, routeId, [alice], [viewers]);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var grants = await db.RouteAccessGrants.AsNoTracking()
            .Where(g => g.RouteId == routeId)
            .Select(g => new { g.UserId, g.GroupId })
            .ToListAsync(Ct);

        Assert.Equal(2, grants.Count);
        Assert.Equal([alice], grants.Where(g => g.UserId is not null).Select(g => g.UserId!.Value));
        Assert.Equal([viewers], grants.Where(g => g.GroupId is not null).Select(g => g.GroupId!.Value));
    }

    [Fact]
    public async Task SetAccess_SwitchingAwayFromRestricted_ClearsGroupGrantsToo() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");
        var staff = await host.AddGroupAsync("staff", alice);

        await SetAccessAsync(host, routeId, [alice], [staff]);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Authenticated, null, [alice], null, [staff]));
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Empty(result.Value.GrantedUserIds);
            // Both kinds go together: "not Restricted" must not mean something different depending on how
            // access happened to have been granted.
            Assert.Empty(result.Value.GrantedGroupIds);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.RouteAccessGrants.AnyAsync(g => g.RouteId == routeId, Ct));
        }
    }

    [Fact]
    public async Task SetAccess_RejectsAnUnknownGroupId_BeforeAnyWrite() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");
        var staff = await host.AddGroupAsync("staff", alice);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Restricted, null, [alice], null, [staff, 4040]));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
            Assert.Contains("4040", result.Error.Message);
        }

        // The good half of the command was not applied either: the route keeps its seeded policy, no grant
        // of either kind exists, and nothing was audited.
        await AssertUnchangedSeededPolicyAsync(host, routeId);
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.RouteAccessGrants.AnyAsync(g => g.RouteId == routeId, Ct));
        }
        Assert.Empty(await host.AuditKindsAsync());
    }

    [Fact]
    public async Task SetAccess_WithOmittedGroupIds_KeepsTheUserGrantsAClientPredatingGroupsSent() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");

        await using (var scope = host.Services.CreateAsyncScope()) {
            // A client that never learned about group grants omits the field entirely; that is read as
            // "no group grants" rather than rejected, which is what makes the addition non-breaking.
            var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
                scope.ServiceProvider,
                new SetAccess.Command(routeId, AccessMode.Restricted, null, [alice]));

            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal([alice], result.Value.GrantedUserIds);
            Assert.Empty(result.Value.GrantedGroupIds);
        }
    }

    [Fact]
    public async Task SetAccess_CanGrantBothAUserAndAGroupTheUserIsIn() {
        using var host = AuthTestHost.Start(WithAccessHandlers);
        var routeId = await SeedRouteIdAsync(host);
        var alice = await host.AddUserAsync("alice");
        var staff = await host.AddGroupAsync("staff", alice);

        // Overlapping subjects are not a conflict — they are access twice over, and the two partial unique
        // indexes are per subject kind precisely so the pair can coexist.
        await SetAccessAsync(host, routeId, [alice], [staff]);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(2, await db.RouteAccessGrants.CountAsync(g => g.RouteId == routeId, Ct));
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
        var routeId = await SeedRouteIdAsync(host);

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
        Assert.Empty(await host.AuditKindsAsync());
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
    private static async Task<int> SeedRouteIdAsync(AuthTestHost host) =>
        (await host.AddRouteAsync("demo.example.invalid")).Id;

    /// <summary>Saves a Restricted policy naming both subject kinds.</summary>
    private static async Task SetAccessAsync(
        AuthTestHost host, int routeId, IReadOnlyList<int> userIds, IReadOnlyList<int> groupIds) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
            scope.ServiceProvider,
            new SetAccess.Command(routeId, AccessMode.Restricted, null, userIds, null, groupIds));
        Assert.True(result.IsSuccess, Describe(result));
    }

    /// <summary>Reloads the route and asserts its policy is still exactly what <see cref="SeedRouteIdAsync"/> left.</summary>
    private static async Task AssertUnchangedSeededPolicyAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
        Assert.Equal(AccessMode.Public, route.AccessMode);
        Assert.Null(route.BypassPaths);
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
