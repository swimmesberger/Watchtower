using Elarion.Abstractions.Serialization;
using Elarion.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the ceiling on the startup reconcile. <see cref="SelfUpdateService.StartAsync"/> runs
/// inside <c>IHost.StartAsync</c>, whose token never fires in this deployment (the host is run with
/// no token and no startup timeout), and the coordinator wait it may perform is on the untimed
/// Docker client. Without a ceiling of its own, a coordinator that is "running" but never exits
/// would hold startup open forever: the app never reaches Started and SIGTERM cannot reach it.
/// </summary>
public sealed class SelfUpdateStartupReconcileTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string CoordinatorId = "c0ffee1234567890abcdef";

    /// <summary>
    /// The apply state is stored as a typed setting, which the serializer can only resolve through
    /// the System module's JSON context — the real host contributes it via the module bootstrapper,
    /// which the test host does not run.
    /// </summary>
    private static readonly Action<IServiceCollection> WithSystemJson = services =>
        services.ConfigureElarionJson(o =>
            o.TypeInfoResolvers.Add(Modules.System.SystemModule.GetJsonTypeInfoResolver()));

    [Fact]
    public async Task StartupGivesUpOnACoordinatorThatNeverExits() {
        using var host = AuthTestHost.Start(WithSystemJson);
        await SeedInterruptedApplyAsync(host);

        // The daemon says the coordinator is running and then never answers the wait.
        using var estate = DockerClientEstate.Create(
            pruneTimeout: TimeSpan.FromMinutes(30), hangLongRunning: true);
        using var service = NewService(host, estate, startupReconcileTimeout: TimeSpan.FromMilliseconds(200));

        // The real host passes CancellationToken.None here, and the daemon never answers, so the
        // ceiling is the only thing that can end this. Raced against a watchdog rather than simply
        // awaited: without the ceiling this would hang the whole test run instead of failing.
        var startup = service.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(startup, Task.Delay(TimeSpan.FromSeconds(10), Ct));

        Assert.Same(startup, finished);
        await startup;
        Assert.Contains(estate.LongRunning.Requests, r => r.Contains($"/containers/{CoordinatorId}/wait"));

        // Giving up leaves the apply state exactly as it was, for a later reconcile to resolve —
        // the behaviour the 100-second client default used to produce.
        var runtime = await LoadRuntimeAsync(host);
        Assert.Equal("restarting", runtime.ApplyStage);
        Assert.Equal(CoordinatorId, runtime.CoordinatorId);
    }

    [Fact]
    public async Task StartupStillReconcilesACoordinatorThatHasExited() {
        using var host = AuthTestHost.Start(WithSystemJson);
        await SeedInterruptedApplyAsync(host);

        // Nothing hangs here: the wait returns exit code 0, so the apply resolves as it should.
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromMinutes(30));
        using var service = NewService(host, estate, startupReconcileTimeout: TimeSpan.FromMilliseconds(200));

        await service.StartAsync(CancellationToken.None);

        var runtime = await LoadRuntimeAsync(host);
        Assert.Equal("idle", runtime.ApplyStage);
        Assert.Null(runtime.CoordinatorId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Leaves behind what an apply interrupted by its own container recreate leaves behind.</summary>
    private static async Task SeedInterruptedApplyAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        await settings.SetAsync(
            "self.runtime",
            new SelfUpdateRuntime { ApplyStage = "restarting", CoordinatorId = CoordinatorId },
            SettingsScope.Global,
            expectedVersion: null,
            Ct);
    }

    private static async Task<SelfUpdateRuntime> LoadRuntimeAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        return await settings.GetAsync("self.runtime", new SelfUpdateRuntime(), SettingsScope.Global, Ct);
    }

    private static SelfUpdateService NewService(
        AuthTestHost host, DockerClientEstate estate, TimeSpan startupReconcileTimeout) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            estate.Client,
            Options.Create(new WatchtowerOptions()),
            NullLogger<SelfUpdateService>.Instance,
            startupReconcileTimeout);
}
