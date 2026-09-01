using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.PortRoutes;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The ADR-0033 addendum's rename: the three port-route settings move out of the <c>Proxy:Yarp:</c>
/// namespace, because a port route never was the in-process provider's.
/// </summary>
/// <remarks>
/// Two properties carry the whole step. It copies the <em>stored</em> row and never a value the new name
/// already has, so an operator's later edit is not undone by an upgrade; and it decides once, because
/// the old rows are deliberately left in place for a rollback and would otherwise be re-copied on every
/// start. The third case is the one it cannot fix: an environment variable is invisible to the settings
/// store, so an old name pinned there is warned about rather than carried across.
/// </remarks>
public sealed class PortRouteSettingsMigrationTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AllThreeStoredValues_AreCopiedToTheNewNames() {
        using var host = AuthTestHost.Start();
        await SetAsync(host, PortRouteSettingsMigration.LegacyLanNames, "nas.lan, 192.168.1.10");
        await SetAsync(host, PortRouteSettingsMigration.LegacyPorts, "9001,9002");
        await SetAsync(host, PortRouteSettingsMigration.LegacyManagedHostPorts, "9001");

        var copied = await RunAsync(host);

        Assert.Equal(
            [
                WatchtowerSettingPaths.ProxyPortRoutesLanNames,
                WatchtowerSettingPaths.ProxyPortRoutesPorts,
                WatchtowerSettingPaths.ProxyPortRoutesManagedHostPorts,
            ],
            copied);
        Assert.Equal(
            "nas.lan, 192.168.1.10",
            await StoredAsync(host, WatchtowerSettingPaths.ProxyPortRoutesLanNames));
        Assert.Equal("9001,9002", await StoredAsync(host, WatchtowerSettingPaths.ProxyPortRoutesPorts));
        Assert.Equal(
            "9001", await StoredAsync(host, WatchtowerSettingPaths.ProxyPortRoutesManagedHostPorts));

        // The old rows stay: a rollback to a build that still reads them has to find them.
        Assert.Equal("9001,9002", await StoredAsync(host, PortRouteSettingsMigration.LegacyPorts));

        // And it is explicable afterwards: nobody made this change, the upgrade did.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.SingleAsync(e => e.Action == "config.migrate", Ct);
        Assert.Equal("proxy", row.Category);
        Assert.Null(row.Actor);
    }

    /// <summary>
    /// The step runs on every start and the old rows are never deleted, so without the sentinel the
    /// second start would copy the same values back over whatever the operator has since typed into the
    /// new field.
    /// </summary>
    [Fact]
    public async Task TheSentinelStopsASecondRun() {
        using var host = AuthTestHost.Start();
        await SetAsync(host, PortRouteSettingsMigration.LegacyLanNames, "nas.lan");

        Assert.NotEmpty(await RunAsync(host));
        Assert.Equal("true", await StoredAsync(host, WatchtowerSettingPaths.ProxyPortRoutesMigrated));

        // The operator edits the new field, and the next start leaves it exactly as they left it.
        await SetAsync(host, WatchtowerSettingPaths.ProxyPortRoutesLanNames, "nas.local");
        Assert.Empty(await RunAsync(host));
        Assert.Equal("nas.local", await StoredAsync(host, WatchtowerSettingPaths.ProxyPortRoutesLanNames));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "config.migrate", Ct));
    }

    /// <summary>
    /// The new name wins wherever it already says something — including an empty string somebody
    /// deliberately saved, which is the case a value comparison would get wrong.
    /// </summary>
    [Theory]
    [InlineData("nas.local")]
    [InlineData("")]
    public async Task AnAlreadySetNewName_IsNotOverwritten(string existing) {
        using var host = AuthTestHost.Start();
        await SetAsync(host, PortRouteSettingsMigration.LegacyLanNames, "nas.lan");
        await SetAsync(host, WatchtowerSettingPaths.ProxyPortRoutesLanNames, existing);

        Assert.Empty(await RunAsync(host));

        Assert.Equal(existing, await StoredAsync(host, WatchtowerSettingPaths.ProxyPortRoutesLanNames));
    }

    /// <summary>A fresh install has nothing to copy, and still records that the question was answered.</summary>
    [Fact]
    public async Task AFreshInstall_CopiesNothingAndStillRecordsTheDecision() {
        using var host = AuthTestHost.Start();

        Assert.Empty(await RunAsync(host));

        Assert.Equal("true", await StoredAsync(host, WatchtowerSettingPaths.ProxyPortRoutesMigrated));
        Assert.Null(await StoredAsync(host, WatchtowerSettingPaths.ProxyPortRoutesLanNames));
    }

    /// <summary>
    /// The one thing the copy cannot reach. Environment values never enter the settings store, so a
    /// deployment that pinned the old variable silently loses what it stated — which is worth a warning
    /// on every start, not one buried behind the sentinel.
    /// </summary>
    [Fact]
    public async Task AnEnvironmentPinnedOldName_IsWarnedAboutOnEveryStart() {
        using var host = AuthTestHost.Start();
        var log = new RecordingLogger();
        var pins = new EnvironmentSettingPins(["WATCHTOWER__PROXY__YARP__LANNAMES"]);

        await RunAsync(host, pins, log);
        await RunAsync(host, pins, log);

        var warnings = log.Warnings;
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => {
            Assert.Contains("WATCHTOWER__PROXY__YARP__LANNAMES", warning, StringComparison.Ordinal);
            Assert.Contains(
                WatchtowerSettingPaths.ProxyPortRoutesLanNames, warning, StringComparison.Ordinal);
            Assert.Contains("WATCHTOWER__PROXY__PORTROUTES__LANNAMES", warning, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// It is internal plumbing, not a setting: the proxy card must neither offer the sentinel nor report
    /// it as env-pinnable, or an operator would be looking at a field that re-arms a one-time copy.
    /// </summary>
    [Fact]
    public void TheSentinelIsNotPartOfTheProxyCardsSurface() =>
        Assert.DoesNotContain(WatchtowerSettingPaths.ProxyPortRoutesMigrated, GetProxyConfig.ProxyPaths);

    /// <summary>
    /// The host resolves it the way <c>Program.InitializeDatabaseAsync</c> does — a startup step that
    /// cannot be resolved is a startup crash, and the rest of this class constructs it directly.
    /// </summary>
    [Fact]
    public async Task ItIsResolvableFromTheContainer() {
        using var host = AuthTestHost.Start();
        await SetAsync(host, PortRouteSettingsMigration.LegacyLanNames, "nas.lan");

        await using var scope = host.Services.CreateAsyncScope();
        var migration = scope.ServiceProvider.GetRequiredService<PortRouteSettingsMigration>();

        Assert.NotEmpty(await migration.RunAsync(Ct));
        Assert.Equal("nas.lan", await StoredAsync(host, WatchtowerSettingPaths.ProxyPortRoutesLanNames));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the step against the host's real services. <paramref name="pins"/> defaults to a pin set
    /// built from no variables at all — the developer's own environment must not decide the outcome.
    /// </summary>
    private static async Task<IReadOnlyList<string>> RunAsync(
        AuthTestHost host, EnvironmentSettingPins? pins = null, ILogger<PortRouteSettingsMigration>? log = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var migration = new PortRouteSettingsMigration(
            sp.GetRequiredService<ISettingsManager>(),
            pins ?? new EnvironmentSettingPins([]),
            sp.GetRequiredService<AuditLog>(),
            log ?? new RecordingLogger());
        return await migration.RunAsync(Ct);
    }

    private static async Task SetAsync(AuthTestHost host, string path, string value) {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .SetStringAsync(path, value, SettingsScope.Global, expectedVersion: null, Ct);
    }

    private static async Task<string?> StoredAsync(AuthTestHost host, string path) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .GetStringAsync(path, SettingsScope.Global, Ct);
    }

    /// <summary>Keeps the rendered warnings, which is the only thing asserted about the log here.</summary>
    private sealed class RecordingLogger : ILogger<PortRouteSettingsMigration> {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
