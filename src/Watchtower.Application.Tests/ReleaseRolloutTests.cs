using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="ReleaseRolloutService"/>: which stacks a release reaches automatically, and which
/// ones an operator's explicit "deploy latest everywhere" reaches instead
/// (docs/products/design.md §Convergent fan-out, §"Auto-deploy precedence").
/// </summary>
/// <remarks>
/// The predicate is the whole feature, so every clause of it gets a stack that fails it. The deploy
/// queue is a recording double: what is under test is <em>who</em> was enqueued and with which trigger,
/// not what a deploy then does — that is <see cref="ReleaseDeployTests"/>.
/// </remarks>
public sealed class ReleaseRolloutTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The automatic rollout's predicate, one excluded stack per clause. Its own product's other stacks
    /// are the only ones considered at all.
    /// </summary>
    [Fact]
    public async Task EnqueueForProduct_TargetsOnlyRunningLatestTrackingOnChangeStacks() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var releaseId = await host.AddReleaseAsync(productId, "v1");

        var included = await host.AddProductStackAsync("included", productId, AutoDeployMode.OnChange);
        await host.AddProductStackAsync("pinned", productId, AutoDeployMode.OnChange, pinnedReleaseId: releaseId);
        await host.AddProductStackAsync(
            "stopped", productId, AutoDeployMode.OnChange, StackDesiredState.Stopped);
        await host.AddProductStackAsync("off", productId, AutoDeployMode.Off);
        await host.AddProductStackAsync("scheduled", productId, AutoDeployMode.Scheduled);
        // Another product's OnChange stack: the fan-out is per product, not per instance.
        var otherProductId = await host.AddProductAsync("other");
        await host.AddProductStackAsync("other-stack", otherProductId, AutoDeployMode.OnChange);

        var (result, queue) = await EnqueueAsync(host, r => r.EnqueueForProductAsync(productId, Ct));

        Assert.Equal(1, result.StacksEnqueued);
        Assert.Equal([(included, DeployTriggers.Release)], queue.Enqueued);
        Assert.Equal(queue.EventIds, result.DeployEventIds);
    }

    /// <summary>
    /// The operator's rollout reaches <c>Off</c> and <c>Scheduled</c> stacks too — pressing a button is
    /// not the stack deploying by itself, and the canary workflow parks its fleet on <c>Off</c> for
    /// exactly this. Pinned and stopped stacks are still excluded.
    /// </summary>
    [Fact]
    public async Task EnqueueLatestForProduct_IgnoresAutoDeployModeButNotPinsOrStoppedStacks() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var releaseId = await host.AddReleaseAsync(productId, "v1");

        var onChange = await host.AddProductStackAsync("a-onchange", productId, AutoDeployMode.OnChange);
        var off = await host.AddProductStackAsync("b-off", productId, AutoDeployMode.Off);
        var scheduled = await host.AddProductStackAsync("c-scheduled", productId, AutoDeployMode.Scheduled);
        await host.AddProductStackAsync("d-pinned", productId, AutoDeployMode.Off, pinnedReleaseId: releaseId);
        await host.AddProductStackAsync("e-stopped", productId, AutoDeployMode.Off, StackDesiredState.Stopped);

        var (result, queue) = await EnqueueAsync(host, r => r.EnqueueLatestForProductAsync(productId, Ct));

        Assert.Equal(3, result.StacksEnqueued);
        Assert.Equal(
            [(onChange, DeployTriggers.ReleaseManual), (off, DeployTriggers.ReleaseManual),
             (scheduled, DeployTriggers.ReleaseManual)],
            queue.Enqueued);
    }

    /// <summary>A product nothing eligible deploys is a legitimate answer, not an error.</summary>
    [Fact]
    public async Task EnqueueForProduct_ReportsZeroWhenNothingIsEligible() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        await host.AddProductStackAsync("off", productId, AutoDeployMode.Off);

        var (result, queue) = await EnqueueAsync(host, r => r.EnqueueForProductAsync(productId, Ct));

        Assert.Equal(0, result.StacksEnqueued);
        Assert.Empty(result.DeployEventIds);
        Assert.Empty(queue.Enqueued);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs a rollout against a recording queue, in one scope.</summary>
    private static async Task<(ReleaseRolloutResult Result, RecordingDeployQueue Queue)> EnqueueAsync(
        AuthTestHost host, Func<ReleaseRolloutService, Task<ReleaseRolloutResult>> rollout) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var queue = RecordingDeployQueue.Create(host);
        var result = await rollout(new ReleaseRolloutService(db, queue));
        return (result, queue);
    }
}

/// <summary>
/// A deploy queue that records what it was asked to enqueue instead of starting a worker.
/// </summary>
/// <remarks>
/// <see cref="DeployQueueService.Enqueue"/> is virtual precisely so a test can accept work without
/// spawning a worker that clones a repository and shells out to compose on a background thread. Each
/// call still writes a tracking event, because callers hand the id back to their own callers.
/// </remarks>
internal sealed class RecordingDeployQueue : DeployQueueService {
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly List<(int StackId, string TriggeredBy)> _enqueued = [];
    private readonly List<int> _eventIds = [];

    private RecordingDeployQueue(
        IServiceScopeFactory scopeFactory, GitCloneService git, ComposeCliService compose,
        DockerEngineClient docker, DeployOutputBroadcaster broadcaster, IProxyProvider proxy,
        Microsoft.Extensions.Options.IOptionsMonitor<Config.WatchtowerOptions> options)
        : base(scopeFactory, git, compose, docker, broadcaster, proxy, options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeployQueueService>.Instance) =>
        _scopeFactory = scopeFactory;

    /// <summary>Builds one over a test host's services.</summary>
    public static RecordingDeployQueue Create(AuthTestHost host) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            new StubGitCloneService(),
            new RecordingComposeCliService(),
            host.Services.GetRequiredService<DockerEngineClient>(),
            host.Services.GetRequiredService<DeployOutputBroadcaster>(),
            host.Services.GetRequiredService<CaddyManager>(),
            host.Services.GetRequiredService<
                Microsoft.Extensions.Options.IOptionsMonitor<Config.WatchtowerOptions>>());

    /// <summary>Every enqueue, in call order.</summary>
    public IReadOnlyList<(int StackId, string TriggeredBy)> Enqueued {
        get { lock (_enqueued) return [.. _enqueued]; }
    }

    /// <summary>The tracking event ids handed back, in call order.</summary>
    public IReadOnlyList<int> EventIds {
        get { lock (_enqueued) return [.. _eventIds]; }
    }

    public override DeployEnqueueResult Enqueue(
        int stackId, string triggeredBy, IReadOnlyList<string>? removeVolumes = null) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var deployEvent = new DeployEvent {
            StackId = stackId, TriggeredBy = triggeredBy, Status = "queued", StartedAt = DateTimeOffset.UtcNow,
        };
        db.DeployEvents.Add(deployEvent);
        db.SaveChanges();
        lock (_enqueued) {
            _enqueued.Add((stackId, triggeredBy));
            _eventIds.Add(deployEvent.Id);
        }
        return new DeployEnqueueResult(deployEvent.Id, "queued");
    }
}
