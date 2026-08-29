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
/// Covers the two ceilings around the coordinator wait, which runs on the untimed Docker client and
/// is therefore bounded by nothing else. At startup the wait sits inside <c>IHost.StartAsync</c>,
/// whose token never fires in this deployment (the host is run with no token and no startup
/// timeout) — a coordinator that never exits would hold startup open forever, with SIGTERM unable
/// to reach it. On the live apply path the same wait holds the apply mutex, so a wedged coordinator
/// would have every retry rejected until the process restarts. Both ceilings cover the wait only:
/// the bookkeeping that follows has to finish, or the runtime record is left half-written.
/// </summary>
[Collection(HostnameEnvironment.Name)]
public sealed class SelfUpdateReconcileCeilingTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string CoordinatorId = RecordingHandler.CreatedContainerId;

    /// <summary>
    /// The apply state is stored as a typed setting, which the serializer can only resolve through
    /// the System module's JSON context — the real host contributes it via the module bootstrapper,
    /// which the test host does not run.
    /// </summary>
    private static readonly Action<IServiceCollection> WithSystemJson = services =>
        services.ConfigureElarionJson(o =>
            o.TypeInfoResolvers.Add(Modules.System.SystemModule.GetJsonTypeInfoResolver()));

    // ── The startup ceiling ───────────────────────────────────────────────────

    [Fact]
    public async Task StartupGivesUpOnACoordinatorThatNeverExits() {
        using var host = AuthTestHost.Start(WithSystemJson);
        await SeedInterruptedApplyAsync(host);

        // The daemon says the coordinator is running and then never answers the wait.
        using var estate = DockerClientEstate.Create(
            pruneTimeout: TimeSpan.FromMinutes(30), hangLongRunning: true);
        using var service = NewService(host, estate, startupReconcileTimeout: TimeSpan.FromSeconds(1));

        // The real host passes CancellationToken.None here, and the daemon never answers, so the
        // ceiling is the only thing that can end this. Raced against a watchdog rather than simply
        // awaited: without the ceiling this would hang the whole test run instead of failing.
        using var watchdog = new CancellationTokenSource();
        var startup = service.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(startup, Task.Delay(TimeSpan.FromSeconds(30), watchdog.Token));
        await watchdog.CancelAsync();

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
        using var service = NewService(host, estate, startupReconcileTimeout: TimeSpan.FromSeconds(1));

        await service.StartAsync(CancellationToken.None);

        var runtime = await LoadRuntimeAsync(host);
        Assert.Equal("idle", runtime.ApplyStage);
        Assert.Null(runtime.CoordinatorId);
    }

    // ── The ceiling covers the wait, not the bookkeeping ──────────────────────

    [Fact]
    public async Task ACeilingThatExpiresAfterTheWaitStillLetsTheBookkeepingFinish() {
        using var host = AuthTestHost.Start(WithSystemJson);
        await SeedInterruptedApplyAsync(host);

        // The wait answers at once; everything after it is slow enough that a ceiling covering the
        // whole reconcile would certainly fire partway through — between clearing the stage and
        // clearing the CoordinatorId, leaving a record nothing ever revisits and the container
        // leaked, because a stage that is no longer "pulling"/"restarting" is never reconciled.
        using var estate = DockerClientEstate.Create(
            pruneTimeout: TimeSpan.FromMinutes(30), defaultDelay: TimeSpan.FromMilliseconds(300));
        using var service = NewService(host, estate, startupReconcileTimeout: TimeSpan.FromSeconds(30));

        await service.ReconcileCoordinatorAsync(
            CoordinatorId, waitTimeout: TimeSpan.FromMilliseconds(100), CancellationToken.None);

        var runtime = await LoadRuntimeAsync(host);
        Assert.Equal("idle", runtime.ApplyStage);
        Assert.Null(runtime.CoordinatorId);
        // The coordinator container was removed rather than left behind as a stopped container.
        Assert.Contains(estate.Default.Requests, r => r.EndsWith($"/containers/{CoordinatorId}"));
    }

    // ── The apply-path ceiling ────────────────────────────────────────────────

    [Fact]
    public async Task AWedgedCoordinatorReleasesTheApplyGate() {
        using var host = AuthTestHost.Start(WithSystemJson);
        // Pulling and spawning are fine; it is the watch afterwards that never resolves. The hang is
        // pinned to the wait alone because the pull shares the untimed client — hanging that instead
        // would stall the apply before it ever reached the watch this test is about.
        using var estate = DockerClientEstate.Create(
            pruneTimeout: TimeSpan.FromMinutes(30),
            hangLongRunning: true,
            hangLongRunningWhen: r => r.RequestUri!.AbsolutePath.EndsWith("/wait"));
        using var service = NewService(
            host, estate,
            startupReconcileTimeout: TimeSpan.FromSeconds(30),
            applyWatchTimeout: TimeSpan.FromMilliseconds(300));

        // Self-detection reads HOSTNAME, which is process-wide. The class sits in the collection every
        // other HOSTNAME-touching test sits in, so none of them run alongside this one.
        var previousHostname = Environment.GetEnvironmentVariable("HOSTNAME");
        Environment.SetEnvironmentVariable("HOSTNAME", "watchtower-self");
        try {
            await service.ApplyUpdateAsync(ct: Ct);

            // While the watch is in flight the apply is the exclusive one.
            var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyUpdateAsync(ct: Ct));
            Assert.Contains("already in progress", busy.Message);

            // Once the watch hits its ceiling the task completes and a retry is accepted again —
            // untimed, the coordinator's silence would lock out every retry until a restart.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true) {
                try {
                    await service.ApplyUpdateAsync(ct: Ct);
                    break;
                } catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress")) {
                    Assert.True(DateTime.UtcNow < deadline, "the wedged coordinator never released the apply gate");
                    await Task.Delay(50, Ct);
                }
            }
        } finally {
            Environment.SetEnvironmentVariable("HOSTNAME", previousHostname);
            // Cancels _cts and waits for the apply task started by the retry above.
            await service.StopAsync(CancellationToken.None);
        }
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
        AuthTestHost host,
        DockerClientEstate estate,
        TimeSpan startupReconcileTimeout,
        TimeSpan? applyWatchTimeout = null) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            estate.Client,
            Options.Create(new WatchtowerOptions()),
            NullLogger<SelfUpdateService>.Instance,
            startupReconcileTimeout,
            applyWatchTimeout ?? TimeSpan.FromSeconds(30));
}
