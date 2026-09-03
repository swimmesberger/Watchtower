using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the route CRUD handlers where they meet ADR-0023: creating, editing and deleting a
/// <see cref="RouteTarget.Watchtower"/> route, and the two things such a route refuses — a stack, and an
/// access policy.
/// </summary>
public sealed class WatchtowerRouteModuleTests {
    private static readonly Action<IServiceCollection> WithRouteHandlers = services => {
        services.AddCreateRoute();
        services.AddUpdateRoute();
        services.AddDeleteRoute();
        services.AddListRoutes();
        services.AddGetRoute();
        services.AddSetAccess();
        services.RemoveAll<IProxyProvider>();
        services.AddSingleton<IProxyProvider, RecordingProxyProvider>();
    };

    /// <summary>A hostname used as the configured <c>Auth:Host</c> in the collision tests.</summary>
    private const string AuthHost = "watchtower.example.invalid";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -- Create ----------------------------------------------------------------------------------

    [Fact]
    public async Task Create_WithTheWatchtowerTarget_StoresAStacklessRouteInTheSystemRealm() {
        using var host = AuthTestHost.Start(WithRouteHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand("ui.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        var dto = result.Value.Route;
        Assert.Equal("watchtower", dto.Target);
        Assert.Null(dto.StackId);
        Assert.Equal(Realm.SystemRealmId, dto.RealmId);
        Assert.Equal(Realm.SystemRealmSlug, dto.RealmSlug);
        // The default: the operator realm had no login host, so the first Watchtower route becomes one.
        // Creating a hostname for the login page and then finding that apps still cannot redirect to it
        // would be a trap.
        Assert.True(dto.IsLoginRoute);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var realm = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
        Assert.Equal(dto.Id, realm.LoginRouteId);
    }

    [Fact]
    public async Task Create_DoesNotStealTheLoginHostFromARealmThatAlreadyHasOne() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var first = await host.AddWatchtowerRouteAsync("ui.example.invalid", makeLoginRoute: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand("admin.example.invalid"));

        // A second UI hostname is an alias, not a change of login address — every redirect and every
        // __wt_sso cookie already points at the first one.
        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(result.Value.Route.IsLoginRoute);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var realm = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
        Assert.Equal(first.Id, realm.LoginRouteId);
    }

    [Fact]
    public async Task Create_TakesOverTheLoginHost_WhenAskedOutright() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        await host.AddWatchtowerRouteAsync("ui.example.invalid", makeLoginRoute: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand("sso.example.invalid") with { MakeLoginRoute = true });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.Route.IsLoginRoute);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var realm = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
        Assert.Equal(result.Value.Route.Id, realm.LoginRouteId);
    }

    [Fact]
    public async Task Create_PlacesTheRouteInTheNamedRealm() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var acme = await host.AddRealmAsync("acme");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand("login.acme.invalid") with { RealmId = acme });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(acme, result.Value.Route.RealmId);
        Assert.Equal("acme", result.Value.Route.RealmSlug);
        Assert.True(result.Value.Route.IsLoginRoute);
    }

    /// <summary>
    /// Refused rather than ignored: a caller that filled in a stack and a port has misunderstood what it
    /// is creating, and silently dropping those values would produce a route serving something else
    /// entirely than the one they described.
    /// </summary>
    [Fact]
    public async Task Create_RefusesAWatchtowerRouteCarryingAStackOrAService() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");

        await using var scope = host.Services.CreateAsyncScope();
        var withStack = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand("ui.example.invalid") with { StackId = stackId });
        var withService = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider,
            SelfCommand("ui.example.invalid") with { ServiceName = "web", ContainerPort = 8080 });

        Assert.Equal(ErrorKind.Validation, withStack.Error.Kind);
        Assert.Equal(ErrorKind.Validation, withService.Error.Kind);
    }

    [Fact]
    public async Task Create_RefusesAnUnknownRealm() {
        using var host = AuthTestHost.Start(WithRouteHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand("ui.example.invalid") with { RealmId = 404 });

        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    /// <summary>
    /// The symmetric half of the guard in <c>system.updateAuthConfig</c>: <c>Auth:Host</c> is the operator
    /// realm's fallback login host, so a <em>customer</em> realm serving Watchtower on it would send
    /// operator visitors to a login page that cannot admit them and give both populations one token
    /// issuer. Whichever of the two is written second is the one refused.
    /// </summary>
    [Fact]
    public async Task Create_RefusesANonSystemRealmsRouteOnTheConfiguredAuthHost() {
        using var host = AuthTestHost.Start(WithRouteHandlers, ("Watchtower:Auth:Host", AuthHost));
        var acme = await host.AddRealmAsync("acme");

        await using var scope = host.Services.CreateAsyncScope();
        var refused = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand(AuthHost) with { RealmId = acme });

        Assert.Equal(ErrorKind.Validation, refused.Error.Kind);
        Assert.Contains("Auth:Host", refused.Error.Message, StringComparison.Ordinal);

        // The operator realm's own route on that hostname is exactly what the fallback is a stand-in
        // for, so it is accepted — and takes over from it.
        var accepted = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand(AuthHost));
        Assert.True(accepted.IsSuccess, Describe(accepted));
        Assert.True(accepted.Value.Route.IsLoginRoute);
    }

    [Fact]
    public async Task Update_RefusesMovingANonSystemRealmsRouteOntoTheConfiguredAuthHost() {
        using var host = AuthTestHost.Start(WithRouteHandlers, ("Watchtower:Auth:Host", AuthHost));
        var acme = await host.AddRealmAsync("acme");
        var route = await host.AddWatchtowerRouteAsync("login.acme.invalid", acme, makeLoginRoute: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(
            scope.ServiceProvider, Edit(route.Id, AuthHost));

        Assert.Equal(ErrorKind.Validation, result.Error.Kind);

        // Nothing moved: a refused edit must not half-apply.
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(
            "login.acme.invalid",
            (await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id, Ct)).Domain);
    }

    [Fact]
    public async Task Create_RefusesAnUnrecognisedTarget() {
        using var host = AuthTestHost.Start(WithRouteHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand("ui.example.invalid") with { Target = "watchtwer" });

        // Defaulting a typo to "service" would create a forwarded route where the operator asked for the
        // management plane — the wrong direction to fail in.
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task Create_WithNoTarget_StillMeansAServiceRoute() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider,
            new CreateRoute.Command(stackId, "app.example.invalid", "web", 8080, true, false, null));

        // A client that predates ADR-0023 sends no target and means the only kind of route there was.
        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("service", result.Value.Route.Target);
        Assert.Equal(stackId, result.Value.Route.StackId);
        Assert.Null(result.Value.Route.RealmId);
    }

    // -- Update ----------------------------------------------------------------------------------

    [Fact]
    public async Task Update_MovesTheDomain_AndLeavesTheTargetAlone() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddWatchtowerRouteAsync("ui.example.invalid", makeLoginRoute: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(
            scope.ServiceProvider,
            new UpdateRoute.Command(route.Id, "ops.example.invalid", "", 0, TlsEnabled: false, IsPrimary: true));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("ops.example.invalid", result.Value.Route.Domain);
        Assert.Equal("watchtower", result.Value.Route.Target);
        Assert.False(result.Value.Route.TlsEnabled);
        Assert.True(result.Value.Route.IsLoginRoute);
    }

    [Fact]
    public async Task Update_TogglesTheLoginHostDesignation() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddWatchtowerRouteAsync("ui.example.invalid", makeLoginRoute: true);

        await using var scope = host.Services.CreateAsyncScope();
        var released = await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(
            scope.ServiceProvider, Edit(route.Id, "ui.example.invalid") with { MakeLoginRoute = false });
        Assert.True(released.IsSuccess, Describe(released));
        Assert.False(released.Value.Route.IsLoginRoute);

        var designated = await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(
            scope.ServiceProvider, Edit(route.Id, "ui.example.invalid") with { MakeLoginRoute = true });
        Assert.True(designated.IsSuccess, Describe(designated));
        Assert.True(designated.Value.Route.IsLoginRoute);
    }

    [Fact]
    public async Task Update_RefusesAServiceOrPortOnAWatchtowerRoute() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddWatchtowerRouteAsync("ui.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(
            scope.ServiceProvider,
            new UpdateRoute.Command(route.Id, "ui.example.invalid", "web", 8080, true, false));

        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task Update_RefusesToMakeAServiceRouteALoginHost() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddRouteAsync("app.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(
            scope.ServiceProvider,
            new UpdateRoute.Command(route.Id, "app.example.invalid", "web", 8080, true, false,
                MakeLoginRoute: true));

        // A login page cannot be served by a container this instance forwards to; the redirect would land
        // on somebody else's application.
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    // -- Access ----------------------------------------------------------------------------------

    /// <summary>
    /// The structural invariant seen from the API: route access control does not apply to a hostname
    /// Watchtower serves itself. Refused rather than silently accepted as a no-op — an administrator who
    /// thought they had gated a hostname must find out that they have not.
    /// </summary>
    [Fact]
    public async Task SetAccess_RefusesAWatchtowerRoute() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddWatchtowerRouteAsync("ui.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
            scope.ServiceProvider, new SetAccess.Command(route.Id, AccessMode.Authenticated, null, []));

        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("own login", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(
            AccessMode.Public,
            (await db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id, Ct)).AccessMode);
    }

    /// <summary>
    /// The protected-by-default setting (ADR-0035) does not reach a Watchtower route: Watchtower
    /// authenticates its own visitors, and <c>ck_routes_target</c> allows nothing but Public anyway.
    /// </summary>
    [Fact]
    public async Task Create_LeavesAWatchtowerRoutePublic_UnderTheProtectedDefault() {
        using var host = AuthTestHost.Start(WithRouteHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider, SelfCommand("ui.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("public", result.Value.Route.AccessMode);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == result.Value.Route.Id, Ct);
        Assert.Equal(AccessMode.Public, route.AccessMode);
    }

    /// <summary>
    /// Refused rather than ignored, like the stack and the service above: an administrator who thought
    /// they had gated a hostname must find out that they have not.
    /// </summary>
    [Fact]
    public async Task Create_RefusesAWatchtowerRouteCarryingAnAccessPolicy() {
        using var host = AuthTestHost.Start(WithRouteHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRoute.Command, CreateRoute.Response>(
            scope.ServiceProvider,
            SelfCommand("ui.example.invalid") with { AccessMode = AccessMode.Authenticated });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("own login", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Empty(await db.Routes.AsNoTracking().ToListAsync(Ct));
    }

    // -- Delete ----------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_OfALoginRoute_SucceedsAndWarns() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var routeId = await db.Routes.Where(r => r.Domain == "login.acme.invalid").Select(r => r.Id).SingleAsync(Ct);

        var result = await SendAsync<DeleteRoute.Command, DeleteRoute.Response>(
            scope.ServiceProvider, new DeleteRoute.Command(routeId));

        // Allowed: removing a hostname is a legitimate act. But the realm now redirects nobody, and the
        // response says so rather than leaving the operator to find out from a 401.
        Assert.True(result.IsSuccess, Describe(result));
        Assert.NotNull(result.Value.Warning);
        Assert.Contains("acme", result.Value.Warning);

        var realm = await db.Realms.AsNoTracking().SingleAsync(r => r.Id == acme, Ct);
        Assert.Null(realm.LoginRouteId);
        Assert.Contains("route.delete", await host.AuditKindsAsync());
    }

    [Fact]
    public async Task Delete_OfAnOrdinaryRoute_WarnsAboutNothing() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddWatchtowerRouteAsync("admin.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeleteRoute.Command, DeleteRoute.Response>(
            scope.ServiceProvider, new DeleteRoute.Command(route.Id));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Null(result.Value.Warning);
    }

    // -- Listing ---------------------------------------------------------------------------------

    [Fact]
    public async Task List_MarksTheLoginRouteOfEachRealm() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        await host.AddWatchtowerRouteAsync("portal.acme.invalid", acme);
        await host.AddRouteAsync("app.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListRoutes.Query, ListRoutes.Response>(
            scope.ServiceProvider, new ListRoutes.Query());

        Assert.True(result.IsSuccess, Describe(result));
        // Keyed by hostname, so the rows without one are simply not in it — every route this test creates
        // has a domain, and a port route (ADR-0033) would have nothing to key by.
        var byDomain = new Dictionary<string, RouteDto>(StringComparer.Ordinal);
        foreach (var route in result.Value.Routes) {
            if (route.Domain is { } domain) byDomain[domain] = route;
        }
        Assert.True(byDomain["login.acme.invalid"].IsLoginRoute);
        Assert.Equal("acme", byDomain["login.acme.invalid"].RealmSlug);
        // A second Watchtower hostname for the same realm is not the login host.
        Assert.False(byDomain["portal.acme.invalid"].IsLoginRoute);
        Assert.Equal("service", byDomain["app.example.invalid"].Target);
        Assert.Null(byDomain["app.example.invalid"].RealmSlug);
    }

    /// <summary>
    /// Every row says who it admits, so the Routes page can badge a gated hostname without a
    /// <c>proxy.getAccess</c> call per route (ADR-0035).
    /// </summary>
    [Fact]
    public async Task ListRoutes_ReportsEachRoutesAccessMode() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        await host.AddRouteAsync("open.example.invalid");
        await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        await host.AddRouteAsync("secret.example.invalid", AccessMode.Restricted);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListRoutes.Query, ListRoutes.Response>(
            scope.ServiceProvider, new ListRoutes.Query());

        Assert.True(result.IsSuccess, Describe(result));
        var byDomain = result.Value.Routes
            .Where(r => r.Domain is not null)
            .ToDictionary(r => r.Domain!, r => r.AccessMode, StringComparer.Ordinal);
        Assert.Equal("public", byDomain["open.example.invalid"]);
        Assert.Equal("authenticated", byDomain["app.example.invalid"]);
        Assert.Equal("restricted", byDomain["secret.example.invalid"]);
    }

    // -- Helpers ---------------------------------------------------------------------------------

    /// <summary>A Watchtower-target create command with nothing a Watchtower route may not carry.</summary>
    private static CreateRoute.Command SelfCommand(string domain) =>
        new(StackId: 0, domain, ServiceName: "", ContainerPort: 0, TlsEnabled: true, IsPrimary: false,
            Kind: null, Target: "watchtower");

    /// <summary>An edit that changes nothing but the fields a caller sets on top of it.</summary>
    private static UpdateRoute.Command Edit(int id, string domain) =>
        new(id, domain, ServiceName: "", ContainerPort: 0, TlsEnabled: true, IsPrimary: false);

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
