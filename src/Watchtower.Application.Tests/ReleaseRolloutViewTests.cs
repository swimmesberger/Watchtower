using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Products;
using Watchtower.Application.Modules.Products.Handlers;
using Watchtower.Application.Modules.Stacks.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The partial-failure surface of a fan-out: <c>products.getReleaseRollout</c>,
/// <c>products.retryFailedRollout</c>, and the release chip <c>DeployEventDto</c> now carries.
/// </summary>
public sealed class ReleaseRolloutViewTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── products.getReleaseRollout ───────────────────────────────────────────

    /// <summary>One row per stack of the product, with the counts the header renders.</summary>
    [Fact]
    public async Task GetReleaseRollout_GroupsTheEventsAndCountsThem() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var ok = await host.AddProductStackAsync("a", productId);
        var bad = await host.AddProductStackAsync("b", productId);
        var busy = await host.AddProductStackAsync("c", productId);
        var waiting = await host.AddProductStackAsync("d", productId);
        await host.AddReleaseDeployEventAsync(ok, v1, "success");
        await host.AddReleaseDeployEventAsync(bad, v1, "failed");
        await host.AddReleaseDeployEventAsync(busy, v1, "running");
        await host.AddReleaseDeployEventAsync(waiting, v1, "queued");

        var result = await RolloutAsync(host, v1);

        Assert.True(result.IsSuccess, Describe(result));
        var rollout = result.Value.Rollout;
        Assert.Equal("v1", rollout.Version);
        Assert.Equal(1, rollout.Succeeded);
        Assert.Equal(1, rollout.Failed);
        Assert.Equal(1, rollout.Running);
        Assert.Equal(1, rollout.Queued);
        Assert.Equal(0, rollout.Skipped);
        Assert.Equal(4, rollout.Stacks.Count);
        var row = Assert.Single(rollout.Stacks, s => s.StackId == bad);
        Assert.Equal("failed", row.Status);
        Assert.NotNull(row.FinishedAt);
        Assert.NotNull(row.DeployEventId);
        Assert.Null(row.SkipReason);
    }

    /// <summary>
    /// A stack can have several events for one release — a retry, a manual redeploy. The row is about
    /// where it ended up, so the newest wins.
    /// </summary>
    [Fact]
    public async Task GetReleaseRollout_ReportsTheNewestEventPerStack() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("a", productId);
        await host.AddReleaseDeployEventAsync(stackId, v1, "failed");
        await host.AddReleaseDeployEventAsync(stackId, v1, "success");

        var result = await RolloutAsync(host, v1);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(1, result.Value.Rollout.Succeeded);
        Assert.Equal(0, result.Value.Rollout.Failed);
        Assert.Equal("success", Assert.Single(result.Value.Rollout.Stacks).Status);
    }

    /// <summary>
    /// The skipped half: a stack of the product with no event for this release, with the reason its
    /// state gives today. Events of a <em>different</em> release are not this release's rollout.
    /// </summary>
    [Fact]
    public async Task GetReleaseRollout_NamesTheStacksItNeverReachedAndWhy() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var v2 = await host.AddReleaseAsync(productId, "v2");
        var deployed = await host.AddProductStackAsync("a", productId);
        var stopped = await host.AddProductStackAsync(
            "b", productId, desiredState: StackDesiredState.Stopped);
        var pinnedElsewhere = await host.AddProductStackAsync("c", productId, pinnedReleaseId: v1);
        var quiet = await host.AddProductStackAsync("d", productId);
        await host.AddReleaseDeployEventAsync(deployed, v2, "success");
        // An event of the older release must not count as a row of v2's rollout.
        await host.AddReleaseDeployEventAsync(quiet, v1, "success");

        var result = await RolloutAsync(host, v2);

        Assert.True(result.IsSuccess, Describe(result));
        var rollout = result.Value.Rollout;
        Assert.Equal(1, rollout.Succeeded);
        Assert.Equal(3, rollout.Skipped);
        var byStack = rollout.Stacks.ToDictionary(s => s.StackId);
        Assert.Equal(GetReleaseRollout.SkippedStopped, byStack[stopped].SkipReason);
        Assert.Equal(GetReleaseRollout.SkippedPinned, byStack[pinnedElsewhere].SkipReason);
        Assert.Equal(GetReleaseRollout.SkippedNotDeployed, byStack[quiet].SkipReason);
        Assert.Equal(ReleaseRolloutDto.SkippedStatus, byStack[quiet].Status);
    }

    /// <summary>A stack pinned to <em>this</em> release is not "pinned elsewhere" — it simply has no event.</summary>
    [Fact]
    public async Task GetReleaseRollout_DoesNotCallAStackPinnedToThisReleasePinnedElsewhere() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("a", productId, pinnedReleaseId: v1);

        var result = await RolloutAsync(host, v1);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(
            GetReleaseRollout.SkippedNotDeployed,
            Assert.Single(result.Value.Rollout.Stacks, s => s.StackId == stackId).SkipReason);
    }

    // ── products.retryFailedRollout ──────────────────────────────────────────

    /// <summary>
    /// The load-bearing claim: only the failures are re-enqueued. Re-deploying the stacks that already
    /// succeeded would take a working fleet down and back up for nothing.
    /// </summary>
    [Fact]
    public async Task RetryFailedRollout_EnqueuesOnlyTheFailedStacks() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var ok = await host.AddProductStackAsync("a", productId);
        var bad = await host.AddProductStackAsync("b", productId);
        var alsoBad = await host.AddProductStackAsync("c", productId);
        var untouched = await host.AddProductStackAsync("d", productId);
        await host.AddReleaseDeployEventAsync(ok, v1, "success");
        await host.AddReleaseDeployEventAsync(bad, v1, "failed");
        await host.AddReleaseDeployEventAsync(alsoBad, v1, "failed");

        var (result, queue) = await RetryAsync(host, v1);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(2, result.Value.Retried);
        Assert.Equal(0, result.Value.Skipped);
        Assert.Equal(
            [(bad, DeployTriggers.ReleaseManual), (alsoBad, DeployTriggers.ReleaseManual)], queue.Enqueued);
        Assert.DoesNotContain(queue.Enqueued, e => e.StackId == ok || e.StackId == untouched);

        var audit = Assert.Single(await AuditAsync(host, DeployRelease.AuditAction));
        Assert.Equal("shop/v1", audit.Target);
        Assert.Contains("retry of failed deploys", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>A stack that failed and was then fixed is not a failure any more.</summary>
    [Fact]
    public async Task RetryFailedRollout_IgnoresAStackWhoseLaterDeploySucceeded() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("a", productId);
        await host.AddReleaseDeployEventAsync(stackId, v1, "failed");
        await host.AddReleaseDeployEventAsync(stackId, v1, "success");

        var (result, queue) = await RetryAsync(host, v1);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(0, result.Value.Retried);
        Assert.Empty(queue.Enqueued);
    }

    /// <summary>
    /// The two exclusions. A stopped stack refuses deploys, and a stack now pinned elsewhere would
    /// deploy its pin rather than this release — both would make the button report work it did not do.
    /// </summary>
    [Fact]
    public async Task RetryFailedRollout_SkipsStoppedStacksAndOnesPinnedElsewhere() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var v2 = await host.AddReleaseAsync(productId, "v2");
        var stopped = await host.AddProductStackAsync(
            "a", productId, desiredState: StackDesiredState.Stopped);
        var repinned = await host.AddProductStackAsync("b", productId, pinnedReleaseId: v1);
        var pinnedHere = await host.AddProductStackAsync("c", productId, pinnedReleaseId: v2);
        await host.AddReleaseDeployEventAsync(stopped, v2, "failed");
        await host.AddReleaseDeployEventAsync(repinned, v2, "failed");
        await host.AddReleaseDeployEventAsync(pinnedHere, v2, "failed");

        var (result, queue) = await RetryAsync(host, v2);

        Assert.True(result.IsSuccess, Describe(result));
        // Only the one pinned to the release being retried is re-enqueued.
        Assert.Equal(1, result.Value.Retried);
        Assert.Equal(2, result.Value.Skipped);
        Assert.Equal([(pinnedHere, DeployTriggers.ReleaseManual)], queue.Enqueued);
    }

    /// <summary>
    /// Git mode is refused, following the <c>stacks.setRelease</c> precedent: every deploy this
    /// enqueued would clone the branch head, so a "retry" would quietly become a fleet-wide
    /// branch-head deploy.
    /// </summary>
    [Fact]
    public async Task RetryFailedRollout_RefusesAGitModeProduct() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var bad = await host.AddProductStackAsync("a", productId);
        await host.AddReleaseDeployEventAsync(bad, v1, "failed");
        await host.SetReleaseModeAsync(productId, ProductReleaseMode.Git);

        var (result, queue) = await RetryAsync(host, v1);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Empty(queue.Enqueued);
        Assert.Empty(await AuditAsync(host, DeployRelease.AuditAction));
    }

    /// <summary>Nothing failed, so nothing is enqueued and nothing is recorded.</summary>
    [Fact]
    public async Task RetryFailedRollout_WithNoFailures_DoesNothing() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var ok = await host.AddProductStackAsync("a", productId);
        await host.AddReleaseDeployEventAsync(ok, v1, "success");

        var (result, queue) = await RetryAsync(host, v1);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(0, result.Value.Retried);
        Assert.Empty(queue.Enqueued);
        Assert.Empty(await AuditAsync(host, DeployRelease.AuditAction));
    }

    // ── DeployEventDto carries the release ───────────────────────────────────

    /// <summary>
    /// The owed widening: the history row can render a version chip without a lookup per row, and a
    /// pre-stage-4 deploy still renders chip-less.
    /// </summary>
    [Fact]
    public async Task ListDeployEvents_CarriesTheReleaseItApplied() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("a", productId);
        await host.AddReleaseDeployEventAsync(stackId, v1, "success");
        await host.AddReleaseDeployEventAsync(stackId, releaseId: null, "success", trigger: "manual");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await ActivatorUtilities.CreateInstance<ListDeployEvents>(scope.ServiceProvider)
            .HandleAsync(new ListDeployEvents.Query(stackId), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        var events = result.Value.Events;
        Assert.Equal(2, events.Count);
        var withRelease = Assert.Single(events, e => e.ReleaseId != null);
        Assert.Equal(v1, withRelease.ReleaseId);
        Assert.Equal("v1", withRelease.ReleaseVersion);
        var without = Assert.Single(events, e => e.ReleaseId == null);
        Assert.Null(without.ReleaseVersion);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<Result<GetReleaseRollout.Response>> RolloutAsync(
        AuthTestHost host, int releaseId) {
        await using var scope = host.Services.CreateAsyncScope();
        return await ActivatorUtilities.CreateInstance<GetReleaseRollout>(scope.ServiceProvider)
            .HandleAsync(new GetReleaseRollout.Query(releaseId), Ct);
    }

    private static async Task<(Result<RetryFailedRollout.Response> Result, RecordingDeployQueue Queue)>
        RetryAsync(AuthTestHost host, int releaseId) {
        await using var scope = host.Services.CreateAsyncScope();
        var queue = RecordingDeployQueue.Create(host);
        var handler = ActivatorUtilities.CreateInstance<RetryFailedRollout>(scope.ServiceProvider, queue);
        var result = await handler.HandleAsync(new RetryFailedRollout.Command(releaseId), Ct);
        return (result, queue);
    }

    private static async Task<List<AuditEvent>> AuditAsync(AuthTestHost host, string action) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == action).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
