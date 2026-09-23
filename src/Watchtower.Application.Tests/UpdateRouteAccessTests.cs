using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The access half of <c>proxy.updateRoute</c>: one edit changes a route and who reaches it in a single
/// write, validated by the code <c>proxy.setAccess</c> and <c>proxy.createRoute</c> run — which is what lets
/// the Routes page have one Edit action instead of an edit form beside a separate access dialog.
/// </summary>
/// <remarks>
/// The atomicity cases are the point. An edit form that saved the route and then its access in two calls
/// could apply the first and fail the second, leaving a renamed hostname under its old policy; here a refused
/// policy leaves the route's other fields untouched too.
/// </remarks>
public sealed class UpdateRouteAccessTests {
    private static readonly Action<IServiceCollection> WithRouteHandlers = services => {
        services.AddUpdateRoute();
        services.RemoveAll<IProxyProvider>();
        services.AddSingleton<IProxyProvider, RecordingProxyProvider>();
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnEditChangesTheRouteAndItsAccessInOneWrite() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddRouteAsync("old.example.invalid", AccessMode.Authenticated);
        var alice = await host.AddUserAsync("alice");

        var result = await UpdateAsync(host, Edit(route.Id, "new.example.invalid") with {
            AccessMode = AccessMode.Restricted,
            GrantedUserIds = [alice],
        });

        Assert.True(result.IsSuccess, Describe(result));
        var stored = await RouteAsync(host, route.Id);
        Assert.Equal("new.example.invalid", stored.Domain);
        Assert.Equal(AccessMode.Restricted, stored.AccessMode);
        Assert.Equal([alice], await GrantedUsersAsync(host, route.Id));
        var audit = Assert.Single(await AccessAuditAsync(host));
        Assert.Contains("on edit", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditThatSaysNothingAboutAccess_LeavesItAlone() {
        // What every client predating this sends, and what a non-administrator's edit form sends.
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Restricted);
        var alice = await host.AddUserAsync("alice");
        await host.GrantUserAsync(route.Id, alice);

        var result = await UpdateAsync(host, Edit(route.Id, "renamed.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
        var stored = await RouteAsync(host, route.Id);
        Assert.Equal("renamed.example.invalid", stored.Domain);
        Assert.Equal(AccessMode.Restricted, stored.AccessMode);
        Assert.Equal([alice], await GrantedUsersAsync(host, route.Id));
        Assert.Empty(await AccessAuditAsync(host));
    }

    [Fact]
    public async Task ARefusedPolicy_LeavesTheRoutesOtherFieldsUntouchedToo() {
        // Validated in full before anything is staged — the two-call edit this replaces could not promise that.
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);

        var result = await UpdateAsync(host, Edit(route.Id, "renamed.example.invalid") with {
            AccessMode = AccessMode.Authenticated,
            AccessRuleIds = [4242],
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("4242", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal("app.example.invalid", (await RouteAsync(host, route.Id)).Domain);
    }

    [Fact]
    public async Task PartOfAPolicyWithoutItsMode_IsRefused() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        var alice = await host.AddUserAsync("alice");

        var result = await UpdateAsync(host, Edit(route.Id, "app.example.invalid") with { GrantedUserIds = [alice] });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("access mode", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonAdministratorChangingAccess_IsForbidden_AndChangesNothing() {
        using var host = AuthTestHost.Start(WithRouteHandlers, ("Watchtower:Auth:Enabled", "true"));
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);

        await using (var scope = host.Services.CreateAsyncScope()) {
            TestPrincipal.Seed(scope.ServiceProvider, isAdmin: false);
            var result = await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(
                scope.ServiceProvider,
                Edit(route.Id, "renamed.example.invalid") with { AccessMode = AccessMode.Public });

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Forbidden, result.Error.Kind);
        }

        var stored = await RouteAsync(host, route.Id);
        Assert.Equal("app.example.invalid", stored.Domain);
        Assert.Equal(AccessMode.Authenticated, stored.AccessMode);
    }

    [Fact]
    public async Task ANonAdministratorEditingTheRouteAlone_IsAllowed() {
        // The gate is on the access decision, not on editing routes — the same split the create makes.
        using var host = AuthTestHost.Start(WithRouteHandlers, ("Watchtower:Auth:Enabled", "true"));
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);

        await using var scope = host.Services.CreateAsyncScope();
        TestPrincipal.Seed(scope.ServiceProvider, isAdmin: false);
        var result = await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(
            scope.ServiceProvider, Edit(route.Id, "renamed.example.invalid"));

        Assert.True(result.IsSuccess, Describe(result));
    }

    [Fact]
    public async Task AccessOnAWatchtowerRoute_IsRefused() {
        using var host = AuthTestHost.Start(WithRouteHandlers);
        var route = await host.AddWatchtowerRouteAsync("login.example.invalid");

        var result = await UpdateAsync(host, new UpdateRoute.Command(
            route.Id, "login.example.invalid", ServiceName: "", ContainerPort: 0, TlsEnabled: true,
            IsPrimary: false, AccessMode: AccessMode.Authenticated));

        Assert.False(result.IsSuccess);
        Assert.Contains("Watchtower's own login", result.Error.Message, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>An edit of a service route's own fields, saying nothing about access.</summary>
    private static UpdateRoute.Command Edit(int routeId, string domain) =>
        new(routeId, domain, ServiceName: "web", ContainerPort: 8080, TlsEnabled: true, IsPrimary: false);

    private static async Task<Result<UpdateRoute.Response>> UpdateAsync(AuthTestHost host, UpdateRoute.Command command) {
        await using var scope = host.Services.CreateAsyncScope();
        return await SendAsync<UpdateRoute.Command, UpdateRoute.Response>(scope.ServiceProvider, command);
    }

    private static async Task<Route> RouteAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
    }

    private static async Task<List<int>> GrantedUsersAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.RouteAccessGrants.AsNoTracking()
            .Where(g => g.RouteId == routeId && g.UserId != null)
            .Select(g => g.UserId!.Value).OrderBy(id => id).ToListAsync(Ct);
    }

    private static async Task<List<string?>> AccessAuditAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuthEventKinds.RouteAccessChanged)
            .OrderBy(e => e.Id).Select(e => e.Detail).ToListAsync(Ct);
    }

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
