using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The half of ADR-0023's conversion that the migration cannot do: a configured
/// <c>Watchtower:Auth:Host</c> becomes the operator realm's login route.
/// </summary>
/// <remarks>
/// The load-bearing property is that it runs <em>once</em>. An operator who deletes the converted route —
/// because they moved the UI to another hostname, or because they front Watchtower with another proxy and
/// want no route at all — must not find it recreated on the next restart, which is what the sentinel is
/// for. Everything else here is about not touching what somebody else already said.
/// </remarks>
public sealed class LoginHostConversionTests {
    private const string Host = "watchtower.example.invalid";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AConfiguredAuthHost_BecomesTheOperatorRealmsLoginRoute() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", $"  {Host.ToUpperInvariant()}  "));

        Assert.True(await RunAsync(host));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var route = await db.Routes.AsNoTracking().SingleAsync(Ct);
        // Normalised on the way in, because a route domain is stored normalised and configuration is not.
        Assert.Equal(Host, route.Domain);
        Assert.Equal(RouteTarget.Watchtower, route.Target);
        Assert.Null(route.StackId);
        Assert.Equal(Realm.SystemRealmId, route.RealmId);
        Assert.True(route.TlsEnabled);
        Assert.Equal(DomainKind.Managed, route.Kind);
        Assert.Equal(AccessMode.Public, route.AccessMode);

        var system = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
        Assert.Equal(route.Id, system.LoginRouteId);

        // Actor-less on purpose: nobody asked for this, the upgrade did. The rows are what makes the
        // change explicable months later.
        var rows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == "route.convert").ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("proxy", r.Category));
        Assert.All(rows, r => Assert.Null(r.Actor));
    }

    [Fact]
    public async Task WithNoAuthHostConfigured_NothingIsCreated_ButTheQuestionIsClosed() {
        using var host = AuthTestHost.Start();

        Assert.False(await RunAsync(host));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.Routes.AnyAsync(Ct));
        Assert.Equal("true", await StoredAsync(host, WatchtowerSettingPaths.AuthLoginHostsConverted));
    }

    /// <summary>
    /// The realm half of the conversion (the migration's) may already have produced a Watchtower route for
    /// the hostname — the operator realm cannot have one from there, but an operator can. Then there is
    /// nothing to create and only the designation to make.
    /// </summary>
    [Fact]
    public async Task AnExistingWatchtowerRouteForThatHost_IsDesignatedRatherThanDuplicated() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", Host));
        var existing = await host.AddWatchtowerRouteAsync(Host);

        Assert.True(await RunAsync(host));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(existing.Id, (await db.Routes.AsNoTracking().SingleAsync(Ct)).Id);
        var system = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
        Assert.Equal(existing.Id, system.LoginRouteId);
    }

    /// <summary>
    /// The one case where it has to decline: the hostname already serves an application. Quietly
    /// re-pointing it at the management plane would be the worst possible reading of an upgrade.
    /// </summary>
    [Fact]
    public async Task AnAuthHostAlreadyServedByAServiceRoute_IsLeftAlone() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", Host));
        await host.AddRouteAsync(Host);

        Assert.False(await RunAsync(host));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var route = await db.Routes.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(RouteTarget.Service, route.Target);
        var system = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
        Assert.Null(system.LoginRouteId);
    }

    /// <summary>
    /// The migration half converts each realm's own <c>auth_host</c> first, so it can leave a Watchtower
    /// route on the configured <c>Auth:Host</c> that belongs to a <em>customer</em> realm. Re-pointing it
    /// at the operator realm would take that population's login page away; designating it would send
    /// operator visitors to a page that cannot admit them. Neither is an upgrade step's call to make.
    /// </summary>
    [Fact]
    public async Task AWatchtowerRouteForAnotherRealmOnThatHost_IsLeftAlone() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", Host));
        var acme = await host.AddRealmAsync("acme");
        var theirs = await host.AddWatchtowerRouteAsync(Host, acme, makeLoginRoute: true);

        Assert.False(await RunAsync(host));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var route = await db.Routes.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(theirs.Id, route.Id);
        Assert.Equal(acme, route.RealmId);

        var realm = await db.Realms.AsNoTracking().SingleAsync(r => r.Id == acme, Ct);
        Assert.Equal(theirs.Id, realm.LoginRouteId);
        var system = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
        Assert.Null(system.LoginRouteId);
    }

    [Fact]
    public async Task ARealmThatAlreadyHasALoginRoute_KeepsIt() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", Host));
        var designated = await host.AddWatchtowerRouteAsync("ui.example.invalid", makeLoginRoute: true);

        // The hostname is still created — the operator said Watchtower answers there — but the
        // designation is somebody's explicit choice and is not overwritten.
        Assert.True(await RunAsync(host));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.True(await db.Routes.AnyAsync(r => r.Domain == Host, Ct));
        var system = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
        Assert.Equal(designated.Id, system.LoginRouteId);
    }

    [Fact]
    public async Task ItIsIdempotent_AndDoesNotRecreateADeletedRoute() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", Host));

        Assert.True(await RunAsync(host));
        Assert.False(await RunAsync(host));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.Routes.ExecuteDeleteAsync(Ct);
        }

        // The sentinel closes the question outright: an operator who removed the converted route has
        // said what they want, and a startup step is not entitled to disagree on the next restart.
        Assert.False(await RunAsync(host));
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.Routes.AnyAsync(Ct));
            Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "route.convert", Ct));
        }
    }

    [Fact]
    public async Task WithTheSentinelStored_NothingHappens() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", Host));
        await SetAsync(host, WatchtowerSettingPaths.AuthLoginHostsConverted, "true");

        Assert.False(await RunAsync(host));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.Routes.AnyAsync(Ct));
    }

    /// <summary>
    /// It never clears <c>Auth:Host</c>: the setting keeps working as the operator realm's fallback, so an
    /// instance fronted by somebody else's proxy is not changed in any way that matters.
    /// </summary>
    [Fact]
    public async Task TheConfiguredHost_IsLeftInPlace() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", Host));

        await RunAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var options = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<WatchtowerOptions>>();
        Assert.Equal(Host, options.CurrentValue.Auth.Host);
    }

    /// <summary>
    /// The host resolves it the way <c>Program.InitializeDatabaseAsync</c> does — a startup step that
    /// cannot be resolved is a startup crash.
    /// </summary>
    [Fact]
    public async Task ItIsResolvableFromTheContainer() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", Host));

        await using var scope = host.Services.CreateAsyncScope();
        var conversion = scope.ServiceProvider.GetRequiredService<LoginHostConversion>();

        Assert.True(await conversion.RunAsync(Ct));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private static async Task<bool> RunAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LoginHostConversion>().RunAsync(Ct);
    }

    private static async Task<string?> StoredAsync(AuthTestHost host, string path) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .GetStringAsync(path, SettingsScope.Global, Ct);
    }

    private static async Task SetAsync(AuthTestHost host, string path, string value) {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .SetStringAsync(path, value, SettingsScope.Global, expectedVersion: null, Ct);
    }
}
