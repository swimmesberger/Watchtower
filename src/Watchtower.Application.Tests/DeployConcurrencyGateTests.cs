using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the instance-wide deploy gate: <c>Watchtower:MaxConcurrentDeploys</c> bounds how many
/// <em>different</em> stacks deploy at once (ADR-0026).
/// </summary>
/// <remarks>
/// One worker per stack and no global cap is fine while deploys are things an operator clicks; it stops
/// being fine the moment one action starts them in bulk — <c>templates.deployAll</c> today, a release
/// fan-out over hundreds of tenants next — because every one of them clones, pulls and brings services
/// up against a single registry and a single Docker daemon. The gate is the bound, and the per-stack
/// queue above it is untouched.
/// </remarks>
public sealed class DeployConcurrencyGateTests {
    [Fact]
    public async Task Deploys_OfDifferentStacksNeverExceedTheConfiguredLimit() {
        const int limit = 2;
        const int stacks = 6;
        using var host = AuthTestHost.Start(("Watchtower:MaxConcurrentDeploys", limit.ToString()));
        var compose = new ConcurrencyProbeComposeCliService { Hold = TimeSpan.FromMilliseconds(200) };
        using var queue = CreateQueue(host, compose);

        var deploys = new List<Task>();
        for (var i = 0; i < stacks; i++) {
            var stackId = await host.AddStackAsync($"stack-{i}");
            var eventId = await AddDeployEventAsync(host, stackId);
            deploys.Add(Task.Run(
                () => queue.ExecuteDeployAsync(
                    stackId, eventId, DeployTriggers.Manual, removeVolumes: null,
                    TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));
        }
        await Task.WhenAll(deploys);

        // Every stack deployed — the gate delays work, it never drops it.
        Assert.Equal(stacks, compose.Observed);
        // The claim is the ceiling. How close a given machine gets to it is scheduling, not behaviour:
        // asserting the peak was exactly the limit would fail on a host that happened to run the
        // deploys one after another anyway.
        Assert.InRange(compose.Peak, 1, limit);
    }

    /// <summary>
    /// A deploy waiting for a slot is queued, not running, and the wait is named in its output rather
    /// than looking like a deploy that has stalled.
    /// </summary>
    [Fact]
    public async Task ADeployWaitingForASlot_ReportsItselfAsQueued() {
        using var host = AuthTestHost.Start(("Watchtower:MaxConcurrentDeploys", "1"));
        var compose = new ConcurrencyProbeComposeCliService { Hold = TimeSpan.FromMilliseconds(500) };
        using var queue = CreateQueue(host, compose);
        var ct = TestContext.Current.CancellationToken;

        var firstId = await host.AddStackAsync("first");
        var secondId = await host.AddStackAsync("second");
        var firstEvent = await AddDeployEventAsync(host, firstId);
        var secondEvent = await AddDeployEventAsync(host, secondId);

        var first = Task.Run(() => queue.ExecuteDeployAsync(firstId, firstEvent, DeployTriggers.Manual, null, ct), ct);
        // Let the first deploy take the only permit before the second one asks for it.
        await WaitUntilAsync(async () => await StatusOfAsync(host, firstEvent) == "running", ct);
        var second = Task.Run(() => queue.ExecuteDeployAsync(secondId, secondEvent, DeployTriggers.Manual, null, ct), ct);

        // While the first holds the permit, the second is exactly what it says it is: queued.
        await WaitUntilAsync(
            async () => (await OutputOfAsync(host, secondEvent)).Contains(
                "Waiting for a deploy slot", StringComparison.Ordinal),
            ct);
        Assert.Equal("queued", await StatusOfAsync(host, secondEvent));

        await Task.WhenAll(first, second);
        Assert.Equal("success", await StatusOfAsync(host, secondEvent));
    }

    /// <summary>
    /// A stack stopped while its deploy sits at the gate must not be brought back up when the permit
    /// finally arrives.
    /// </summary>
    /// <remarks>
    /// <c>stacks.stop</c> writes the desired state (ADR-0025); it does not cancel a deploy that is
    /// already under way, and before the gate existed there was no window worth worrying about. A wait
    /// of unbounded length is that window: the check made before the wait says whatever was true
    /// minutes ago, so it is made again on the far side of it.
    /// </remarks>
    [Fact]
    public async Task ADeployStoppedWhileWaitingForASlot_RefusesInsteadOfStartingTheStack() {
        using var host = AuthTestHost.Start(("Watchtower:MaxConcurrentDeploys", "1"));
        var compose = new ConcurrencyProbeComposeCliService { Hold = TimeSpan.FromMilliseconds(500) };
        using var queue = CreateQueue(host, compose);
        var ct = TestContext.Current.CancellationToken;

        var runningId = await host.AddStackAsync("holder");
        var stoppedId = await host.AddStackAsync("stopped-mid-wait");
        var runningEvent = await AddDeployEventAsync(host, runningId);
        var waitingEvent = await AddDeployEventAsync(host, stoppedId);

        var holder = Task.Run(() => queue.ExecuteDeployAsync(runningId, runningEvent, DeployTriggers.Manual, null, ct), ct);
        await WaitUntilAsync(async () => await StatusOfAsync(host, runningEvent) == "running", ct);
        var waiting = Task.Run(() => queue.ExecuteDeployAsync(stoppedId, waitingEvent, DeployTriggers.Manual, null, ct), ct);
        await WaitUntilAsync(
            async () => (await OutputOfAsync(host, waitingEvent)).Contains(
                "Waiting for a deploy slot", StringComparison.Ordinal),
            ct);

        // The operator stops the stack while its deploy is parked.
        await SetDesiredStateAsync(host, stoppedId, StackDesiredState.Stopped);
        await Task.WhenAll(holder, waiting);

        Assert.Equal("failed", await StatusOfAsync(host, waitingEvent));
        Assert.Contains("Stack is stopped", await OutputOfAsync(host, waitingEvent), StringComparison.Ordinal);
        // Only the holder ever reached compose; the parked deploy refused before running anything.
        Assert.Equal(1, compose.Observed);
        Assert.Equal("success", await StatusOfAsync(host, runningEvent));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A queue of this test's own, so the gate under test is sized from this host's configuration and
    /// isolated from every other test in the assembly.
    /// </summary>
    private static DeployQueueService CreateQueue(AuthTestHost host, ComposeCliService compose) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            new StubGitCloneService(),
            compose,
            host.Services.GetRequiredService<DockerEngineClient>(),
            host.Services.GetRequiredService<DeployOutputBroadcaster>(),
            host.Services.GetRequiredService<CaddyManager>(),
            host.Services.GetRequiredService<HostGpuProbe>(),
            host.Services.GetRequiredService<IOptionsMonitor<WatchtowerOptions>>(),
            NullLogger<DeployQueueService>.Instance);

    private static async Task<int> AddDeployEventAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var deployEvent = new DeployEvent {
            StackId = stackId, TriggeredBy = "test", Status = "queued", StartedAt = DateTimeOffset.UtcNow,
        };
        db.DeployEvents.Add(deployEvent);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return deployEvent.Id;
    }

    /// <summary>What <c>stacks.stop</c> writes: the desired state, and nothing else (ADR-0025).</summary>
    private static async Task SetDesiredStateAsync(AuthTestHost host, int stackId, StackDesiredState state) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.DesiredState, state), TestContext.Current.CancellationToken);
    }

    private static async Task<string> StatusOfAsync(AuthTestHost host, int eventId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.DeployEvents.AsNoTracking().Where(e => e.Id == eventId)
            .Select(e => e.Status).FirstAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> OutputOfAsync(AuthTestHost host, int eventId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.DeployEvents.AsNoTracking().Where(e => e.Id == eventId)
            .Select(e => e.Output).FirstAsync(TestContext.Current.CancellationToken) ?? string.Empty;
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, failing the test if it never does.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct) {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline) {
            if (await condition()) return;
            await Task.Delay(20, ct);
        }
        Assert.Fail("The condition was still not true after 30 seconds.");
    }
}
