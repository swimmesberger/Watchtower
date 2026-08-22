using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The upgrade guard behind ADR-0020's default flip. Before it, <c>Proxy:Provider</c> defaulted to
/// <c>caddy</c>, so an operator who added routes never had to name a provider — and after the flip that
/// same silence would mean "the in-process proxy", abandoning a working Caddy container and its
/// certificates on nothing but an image update.
/// </summary>
/// <remarks>
/// The load-bearing property is that it decides <em>once</em>. Routes are evidence of the old default
/// only on the first start after the upgrade; a fresh install adds routes of its own soon enough, and a
/// rule that re-read the table every start would eventually drag it onto Caddy. Half the cases below are
/// about that second start rather than the first.
/// </remarks>
public sealed class ProxyProviderMigrationTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The environment of an instance that pins the provider — the one pin that matters here.</summary>
    private const string ProviderVariable = "WATCHTOWER__PROXY__PROVIDER";

    [Fact]
    public async Task AnExistingProxyInstallWithNoStatedProvider_IsPinnedToCaddy() {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Enabled", "true"));
        await host.AddRouteAsync("app.example.invalid");

        Assert.True(await RunAsync(host));

        Assert.Equal(ProxyProviderNames.Caddy, await StoredProviderAsync(host));
        // And it is explicable afterwards: nobody made this change, the upgrade did.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.SingleAsync(e => e.Action == "config.migrate", Ct);
        Assert.Equal("proxy", row.Category);
        Assert.Null(row.Actor);
    }

    /// <summary>
    /// The proxy toggle is not part of the evidence. A pre-flip instance with routes was a Caddy
    /// installation whatever position that switch happens to be in right now, and an operator who turned
    /// the proxy off to investigate something must not find it has changed provider when they turn it
    /// back on.
    /// </summary>
    [Fact]
    public async Task APreFlipInstallWhoseProxyIsCurrentlyDisabled_IsStillPinned() {
        using var host = AuthTestHost.Start();
        await host.AddRouteAsync("app.example.invalid");

        Assert.True(await RunAsync(host));

        Assert.Equal(ProxyProviderNames.Caddy, await StoredProviderAsync(host));
    }

    /// <summary>
    /// The case the sentinel exists for. A fresh install starts with an empty route table and is entitled
    /// to the new default — but nothing writes the provider row in normal use, so on the restart after it
    /// creates its first route it would satisfy every other condition and be pinned to a provider it has
    /// never run. The first start has to mark the question closed.
    /// </summary>
    [Fact]
    public async Task AFreshInstall_IsNotPinnedOnALaterStartOnceItHasRoutes() {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Enabled", "true"));

        // First start: no routes, so nothing to preserve — and the decision is recorded as made.
        Assert.False(await RunAsync(host));
        Assert.Null(await StoredProviderAsync(host));
        Assert.Equal("true", await StoredAsync(host, WatchtowerSettingPaths.ProxyProviderMigrated));

        // The operator now uses the thing: routes appear, and nothing writes Proxy:Provider on that path.
        await host.AddRouteAsync("app.example.invalid");

        // Any later start. Without the sentinel this is indistinguishable from a pre-flip install.
        Assert.False(await RunAsync(host));
        Assert.Null(await StoredProviderAsync(host));
    }

    /// <summary>The sentinel closes the question outright, whatever the rest of the state says.</summary>
    [Fact]
    public async Task WithTheSentinelStored_NothingHappens() {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Enabled", "true"));
        await host.AddRouteAsync("app.example.invalid");
        await SetAsync(host, WatchtowerSettingPaths.ProxyProviderMigrated, "true");

        Assert.False(await RunAsync(host));

        Assert.Null(await StoredProviderAsync(host));
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.AuditEvents.AnyAsync(e => e.Action == "config.migrate", Ct));
    }

    /// <summary>
    /// The step runs on every start, so "it pinned once and then stopped" is the whole contract — and the
    /// sentinel, not the provider row, is what enforces it.
    /// </summary>
    [Fact]
    public async Task ItIsIdempotent() {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Enabled", "true"));
        await host.AddRouteAsync("app.example.invalid");

        Assert.True(await RunAsync(host));
        Assert.False(await RunAsync(host));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "config.migrate", Ct));
    }

    /// <summary>
    /// An env var is a stated provider even though the settings store is empty — and env wins over the
    /// store (ADR-0014), so a row written underneath one would persist a value that never takes effect.
    /// The sentinel is still written: the question has been answered, just in the negative.
    /// </summary>
    [Fact]
    public async Task AnEnvironmentPinnedProvider_IsLeftAlone() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", ProxyProviderNames.Yarp));
        await host.AddRouteAsync("app.example.invalid");

        Assert.False(await RunAsync(host, new EnvironmentSettingPins([ProviderVariable])));

        Assert.Null(await StoredProviderAsync(host));
        Assert.Equal("true", await StoredAsync(host, WatchtowerSettingPaths.ProxyProviderMigrated));
    }

    /// <summary>
    /// A stored provider is a decision someone already made — including the operator who deliberately
    /// moved an existing install onto the in-process proxy, whose choice this must not undo.
    /// </summary>
    [Theory]
    [InlineData(ProxyProviderNames.Yarp)]
    [InlineData(ProxyProviderNames.Caddy)]
    [InlineData(ProxyProviderNames.Cloudflare)]
    public async Task AStoredProvider_IsLeftAlone(string stored) {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Enabled", "true"));
        await host.AddRouteAsync("app.example.invalid");
        await SetAsync(host, WatchtowerSettingPaths.ProxyProvider, stored);

        Assert.False(await RunAsync(host));

        Assert.Equal(stored, await StoredProviderAsync(host));
    }

    /// <summary>
    /// It is internal plumbing, not a setting: the proxy card must neither offer it nor report it as
    /// env-pinnable, or an operator would be looking at a checkbox that re-arms a one-time migration.
    /// </summary>
    [Fact]
    public void TheSentinelIsNotPartOfTheProxyCardsSurface() =>
        Assert.DoesNotContain(WatchtowerSettingPaths.ProxyProviderMigrated, GetProxyConfig.ProxyPaths);

    /// <summary>
    /// The host resolves it the way <c>Program.InitializeDatabaseAsync</c> does. The rest of this class
    /// constructs it directly to control the pin set, which would not notice the registration going
    /// missing — and a startup step that cannot be resolved is a startup crash.
    /// </summary>
    [Fact]
    public async Task ItIsResolvableFromTheContainer() {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Enabled", "true"));
        await host.AddRouteAsync("app.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var migration = scope.ServiceProvider.GetRequiredService<ProxyProviderMigration>();

        Assert.True(await migration.RunAsync(Ct));
        Assert.Equal(ProxyProviderNames.Caddy, await StoredProviderAsync(host));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the step against the host's real services. <paramref name="pins"/> defaults to a pin set
    /// built from no variables at all — the developer's own environment must not decide the outcome.
    /// </summary>
    private static async Task<bool> RunAsync(AuthTestHost host, EnvironmentSettingPins? pins = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var migration = new ProxyProviderMigration(
            sp.GetRequiredService<WatchtowerDbContext>(),
            sp.GetRequiredService<ISettingsManager>(),
            pins ?? new EnvironmentSettingPins([]),
            sp.GetRequiredService<AuditLog>(),
            NullLogger<ProxyProviderMigration>.Instance);
        return await migration.RunAsync(Ct);
    }

    private static Task<string?> StoredProviderAsync(AuthTestHost host) =>
        StoredAsync(host, WatchtowerSettingPaths.ProxyProvider);

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
