using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Stacks.Handlers;
using Watchtower.Application.Modules.Tenancy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The two chains stage 7 adds (design.md §"Backups across tenants"): <b>back up, then deploy</b> and
/// <b>back up, then tear the tenant down</b> — each running only if the backup succeeded, and each
/// leaving a legible trail when it did not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the chain is its own object.</b> The backup queue is single-flight process-wide and the
/// deploy queue is per stack behind an instance-wide gate; neither can express "and then the other
/// one". <see cref="BackupChainCoordinator"/> holds that relationship, keyed by the backup event id —
/// which is also the key coalescing collapses onto, so two pre-deploy requests for one stack become one
/// backup with both follow-ups hanging off it.
/// </para>
/// <para>
/// <b>Every blocking rule here is worth mutation-checking.</b> "The deploy did not run" is the whole
/// safety property, and a coordinator that ran the follow-up regardless would pass any test that only
/// asserted the happy path.
/// </para>
/// </remarks>
public sealed class BackupChainTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The coordinator itself ───────────────────────────────────────────────

    /// <summary>A successful backup releases the deploy, under the trigger the caller intended.</summary>
    [Fact]
    public async Task ASuccessfulBackup_ReleasesTheChainedDeploy() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var stackId = await host.AddProductStackAsync("shop-acme", productId);
        var (coordinator, deploys) = Coordinator(host);

        coordinator.Attach(1, BackupChainStep.ForDeploy(stackId, DeployTriggers.ReleaseManual));
        await coordinator.OnBackupFinishedAsync(1, success: true, Ct);

        Assert.Equal([(stackId, DeployTriggers.ReleaseManual)], deploys.Enqueued);
        Assert.False(coordinator.HasPending(1));
    }

    /// <summary>
    /// <b>The safety property.</b> A failed pre-deploy backup blocks that stack's deploy — nothing is
    /// enqueued — and the refusal is written where an operator looks for the deploy that did not happen:
    /// a failed deploy event on the stack, naming the backup run.
    /// </summary>
    [Fact]
    public async Task AFailedBackup_BlocksTheDeploy_AndLeavesAFailedDeployEventSayingWhy() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var stackId = await host.AddProductStackAsync("shop-acme", productId);
        var (coordinator, deploys) = Coordinator(host);

        coordinator.Attach(77, BackupChainStep.ForDeploy(stackId, DeployTriggers.ReleaseManual));
        await coordinator.OnBackupFinishedAsync(77, success: false, Ct);

        Assert.Empty(deploys.Enqueued);
        var blocked = Assert.Single(await DeployEventsAsync(host, stackId));
        Assert.Equal("failed", blocked.Status);
        Assert.Equal(DeployTriggers.ReleaseManual, blocked.TriggeredBy);
        Assert.NotNull(blocked.FinishedAt);
        Assert.Contains("pre-deploy backup failed", blocked.Output!, StringComparison.Ordinal);
        Assert.Contains("#77", blocked.Output!, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", blocked.Output!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Coalescing is the reason the key is the backup event: two callers, one backup, both follow-ups.
    /// Losing one of them would silently drop a tenant out of a rollout.
    /// </summary>
    [Fact]
    public async Task TwoStepsOnOneBackup_BothRun() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var stackId = await host.AddProductStackAsync("shop-acme", productId);
        var (coordinator, deploys) = Coordinator(host);

        coordinator.Attach(5, BackupChainStep.ForDeploy(stackId, DeployTriggers.ReleaseManual));
        coordinator.Attach(5, BackupChainStep.ForDeploy(stackId, DeployTriggers.Manual));
        await coordinator.OnBackupFinishedAsync(5, success: true, Ct);

        Assert.Equal(
            [(stackId, DeployTriggers.ReleaseManual), (stackId, DeployTriggers.Manual)],
            deploys.Enqueued);
    }

    /// <summary>
    /// <b>Coalescing, through the real queue.</b> Two pre-deploy requests for one stack — a double
    /// click, or two callers in a rollout — must produce <em>one</em> backup with <em>both</em>
    /// follow-ups, and both must fire. Driven through <see cref="BackupQueueService.Enqueue"/> rather
    /// than <c>Attach</c>, because the coalescing branch is the thing under test: attaching by hand
    /// would test the dictionary and skip the decision that fills it.
    /// </summary>
    /// <remarks>
    /// The worker loop is never started (the host does not run hosted services), so the queued job sits
    /// in the channel and the second call sees the first still pending — exactly the state a real
    /// double-click hits. The chain is then released by hand.
    /// </remarks>
    [Fact]
    public async Task TwoChainedEnqueuesForOneStack_CoalesceOntoOneEvent_AndBothStepsFire() {
        using var host = AuthTestHost.Start(WithRecordedDeployQueue);
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var stackId = await host.AddProductStackAsync("shop-acme", productId);
        var queue = host.Services.GetRequiredService<BackupQueueService>();
        var chain = host.Services.GetRequiredService<BackupChainCoordinator>();
        var deploys = (RecordingDeployQueue)host.Services.GetRequiredService<DeployQueueService>();

        var first = queue.Enqueue(
            stackId, BackupTriggers.PreDeploy,
            BackupChainStep.ForDeploy(stackId, DeployTriggers.ReleaseManual));
        var second = queue.Enqueue(
            stackId, BackupTriggers.PreDeploy,
            BackupChainStep.ForDeploy(stackId, DeployTriggers.Manual));

        // One backup, not two: the second request found the first still waiting.
        Assert.Equal(first.BackupEventId, second.BackupEventId);
        Assert.Single(await BackupEventsAsync(host, stackId));

        await chain.OnBackupFinishedAsync(first.BackupEventId, success: true, Ct);

        // Both follow-ups, in the order they were attached — losing the second would silently drop a
        // caller's deploy on the floor with nothing anywhere saying so.
        Assert.Equal(
            [(stackId, DeployTriggers.ReleaseManual), (stackId, DeployTriggers.Manual)],
            deploys.Enqueued);
    }

    /// <summary>A backup nothing was chained to finishes silently — every ordinary run takes this path.</summary>
    [Fact]
    public async Task AnUnchainedBackup_DoesNothing() {
        using var host = AuthTestHost.Start();
        var (coordinator, deploys) = Coordinator(host);

        await coordinator.OnBackupFinishedAsync(123, success: true, Ct);

        Assert.Empty(deploys.Enqueued);
    }

    // ── Pre-deploy: the write paths that attach the chain ────────────────────

    /// <summary>
    /// <c>stacks.setRelease</c> with <c>backupFirst</c> enqueues a backup and <em>no</em> deploy — the
    /// deploy is the chain's to enqueue, so a failed backup leaves nothing to cancel.
    /// </summary>
    [Fact]
    public async Task SetRelease_WithBackupFirst_EnqueuesTheBackupAndChainsTheDeployToIt() {
        using var host = StartWithQueues();
        var productId = await host.AddProductAsync("shop");
        var releaseId = await host.AddReleaseAsync(productId, "1.4.0");
        var stackId = await host.AddProductStackAsync("shop-acme", productId);

        await using var scope = host.Services.CreateAsyncScope();
        var deploys = RecordingDeployQueue.Create(host);
        var handler = ActivatorUtilities.CreateInstance<SetStackRelease>(
            scope.ServiceProvider, deploys, host.Services.GetRequiredService<BackupQueueService>());
        var result = await handler.HandleAsync(
            new SetStackRelease.Command(stackId, releaseId, Deploy: true, BackupFirst: true), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(result.Value.Deployed);
        Assert.Null(result.Value.DeployEventId);
        Assert.NotNull(result.Value.BackupEventId);
        // The pin still landed: the backup guards the deploy, not the write.
        Assert.Equal(releaseId, (await host.ReleaseStateAsync(stackId)).Pinned);

        var queued = Assert.Single(Chained(host));
        Assert.Equal(stackId, queued.StackId);
        Assert.Equal(BackupTriggers.PreDeploy, queued.TriggeredBy);
        Assert.Equal(BackupChainKind.Deploy, queued.Chain!.Kind);
        Assert.Equal(DeployTriggers.ReleaseManual, queued.Chain.DeployTrigger);
        Assert.Empty(deploys.Enqueued);
    }

    /// <summary>Without the flag nothing changes: the deploy is enqueued directly, no backup at all.</summary>
    [Fact]
    public async Task SetRelease_WithoutBackupFirst_DeploysDirectly() {
        using var host = StartWithQueues();
        var productId = await host.AddProductAsync("shop");
        var releaseId = await host.AddReleaseAsync(productId, "1.4.0");
        var stackId = await host.AddProductStackAsync("shop-acme", productId);

        await using var scope = host.Services.CreateAsyncScope();
        var deploys = RecordingDeployQueue.Create(host);
        var handler = ActivatorUtilities.CreateInstance<SetStackRelease>(
            scope.ServiceProvider, deploys, host.Services.GetRequiredService<BackupQueueService>());
        var result = await handler.HandleAsync(
            new SetStackRelease.Command(stackId, releaseId, Deploy: true), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.Deployed);
        Assert.Null(result.Value.BackupEventId);
        Assert.Empty(Chained(host));
        Assert.Equal([(stackId, DeployTriggers.ReleaseManual)], deploys.Enqueued);
    }

    /// <summary>
    /// The fleet path: every running tenant gets a chained backup instead of a deploy, and the response
    /// reports the count that is actually happening rather than a deploy count of zero with no
    /// explanation.
    /// </summary>
    [Fact]
    public async Task SetTenantsRelease_WithBackupFirst_ChainsEveryRunningTenant() {
        using var host = StartWithQueues();
        var productId = await host.AddProductAsync("shop");
        var releaseId = await host.AddReleaseAsync(productId, "1.4.0");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var a = await host.AddProductStackAsync("shop-a", productId, templateId: templateId);
        var b = await host.AddProductStackAsync("shop-b", productId, templateId: templateId);
        // A stopped tenant is pinned and not deployed — and so it is not backed up either.
        await host.AddProductStackAsync(
            "shop-c", productId, templateId: templateId, desiredState: StackDesiredState.Stopped);

        await using var scope = host.Services.CreateAsyncScope();
        var deploys = RecordingDeployQueue.Create(host);
        var handler = ActivatorUtilities.CreateInstance<SetTenantsRelease>(
            scope.ServiceProvider, deploys, host.Services.GetRequiredService<BackupQueueService>());
        var result = await handler.HandleAsync(
            new SetTenantsRelease.Command(templateId, releaseId, Deploy: true, BackupFirst: true), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(3, result.Value.Result.TenantCount);
        Assert.Equal(0, result.Value.Result.Deployed);
        Assert.Equal(2, result.Value.Result.BackedUp);
        Assert.Equal(2, result.Value.Result.BackupEventIds!.Count);
        Assert.Empty(deploys.Enqueued);
        Assert.Equal([a, b], Chained(host).Select(c => c.StackId));
        Assert.All(Chained(host), c => {
            Assert.Equal(BackupTriggers.PreDeploy, c.TriggeredBy);
            Assert.Equal(BackupChainKind.Deploy, c.Chain!.Kind);
        });
    }

    // ── The final backup before a teardown ───────────────────────────────────

    /// <summary>
    /// <c>templates.removeTenant</c> with <c>finalBackup</c> removes nothing yet: it enqueues the backup
    /// and says so, and the teardown is the chain's to run.
    /// </summary>
    [Fact]
    public async Task RemoveTenant_WithFinalBackup_QueuesTheBackupAndLeavesTheTenantStanding() {
        using var host = StartWithQueues();
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("billing-acme", templateId, "acme", "billing-acme");

        var result = await RemoveAsync(host, templateId, "acme", finalBackup: true);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(result.Value.Removed);
        Assert.Equal("acme", result.Value.Slug);
        Assert.NotNull(result.Value.BackupEventId);

        var queued = Assert.Single(Chained(host));
        Assert.Equal(stackId, queued.StackId);
        Assert.Equal(BackupTriggers.Final, queued.TriggeredBy);
        Assert.Equal(BackupChainKind.TenantTeardown, queued.Chain!.Kind);
        Assert.Equal(templateId, queued.Chain.TemplateId);
        Assert.Equal("acme", queued.Chain.Slug);
        Assert.True(await StackExistsAsync(host, stackId));
    }

    /// <summary>The knowable refusals still come back immediately, not four minutes later.</summary>
    [Fact]
    public async Task RemoveTenant_WithFinalBackup_StillRefusesAnUnknownSlugAndAnActiveDeploy() {
        using var host = StartWithQueues();
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("billing-acme", templateId, "acme", "billing-acme");

        var missing = await RemoveAsync(host, templateId, "nosuchtenant", finalBackup: true);
        Assert.False(missing.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, missing.Error.Kind);

        await host.AddDeployEventAsync(stackId, "running");
        var busy = await RemoveAsync(host, templateId, "acme", finalBackup: true);
        Assert.False(busy.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, busy.Error.Kind);
        Assert.Empty(Chained(host));
    }

    /// <summary>A successful final backup tears the tenant down and records that it did.</summary>
    [Fact]
    public async Task ASuccessfulFinalBackup_TearsTheTenantDown() {
        using var host = AuthTestHost.Start(WithComposeAndProxyDoubles);
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("billing-acme", templateId, "acme", "billing-acme");
        var (coordinator, _) = Coordinator(host);

        coordinator.Attach(9, BackupChainStep.ForTenantTeardown(stackId, templateId, "acme", removeVolumes: true));
        await coordinator.OnBackupFinishedAsync(9, success: true, Ct);

        Assert.False(await StackExistsAsync(host, stackId));
        var compose = (StubComposeCliService)host.Services.GetRequiredService<ComposeCliService>();
        Assert.Equal([("billing-acme", true)], compose.Downs);
        var audit = Assert.Single(await AuditAsync(host, "tenant.remove.final-backup"));
        Assert.True(audit.Success);
        Assert.Contains("the tenant was removed", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A failed final backup aborts the removal.</b> The tenant is still there — compose is never even
    /// asked — and the audit trail says the removal was aborted rather than leaving silence where a
    /// tenant used to be expected.
    /// </summary>
    [Fact]
    public async Task AFailedFinalBackup_AbortsTheRemoval() {
        using var host = AuthTestHost.Start(WithComposeAndProxyDoubles);
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("billing-acme", templateId, "acme", "billing-acme");
        var (coordinator, _) = Coordinator(host);

        coordinator.Attach(9, BackupChainStep.ForTenantTeardown(stackId, templateId, "acme", removeVolumes: false));
        await coordinator.OnBackupFinishedAsync(9, success: false, Ct);

        Assert.True(await StackExistsAsync(host, stackId));
        var compose = (StubComposeCliService)host.Services.GetRequiredService<ComposeCliService>();
        Assert.Empty(compose.Downs);
        var audit = Assert.Single(await AuditAsync(host, "tenant.remove.aborted"));
        Assert.False(audit.Success);
        Assert.Contains("the tenant was not removed", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deploy check is re-run by the teardown itself, so a deploy that started <em>during</em> the
    /// final backup still refuses it — and the tenant survives with the outcome recorded.
    /// </summary>
    [Fact]
    public async Task ADeployThatStartedDuringTheFinalBackup_StillRefusesTheTeardown() {
        using var host = AuthTestHost.Start(WithComposeAndProxyDoubles);
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await host.AddStackAsync("billing-acme", templateId, "acme", "billing-acme");
        var (coordinator, _) = Coordinator(host);
        coordinator.Attach(9, BackupChainStep.ForTenantTeardown(stackId, templateId, "acme", removeVolumes: false));

        await host.AddDeployEventAsync(stackId, "running");
        await coordinator.OnBackupFinishedAsync(9, success: true, Ct);

        Assert.True(await StackExistsAsync(host, stackId));
        var audit = Assert.Single(await AuditAsync(host, "tenant.remove.final-backup"));
        Assert.False(audit.Success);
        Assert.Contains("the teardown did not", audit.Detail!, StringComparison.Ordinal);
    }

    // -- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Both queues recorded — nothing reaches Docker, and both halves of a chain are visible — plus a
    /// registry that always says the images are still there, so the pin pre-flight is not what a chain
    /// test is measuring.
    /// </summary>
    private static AuthTestHost StartWithQueues() => AuthTestHost.Start(services => {
        RecordingBackupQueue.Register(services);
        services.AddSingleton<IReleaseDigestResolver>(new AlwaysResolvedDigests());
    });

    /// <summary>Every image resolves; the pin pre-flight is <see cref="ReleasePinRpcTests"/>' subject.</summary>
    private sealed class AlwaysResolvedDigests : IReleaseDigestResolver {
        public Task<ReleaseDigestResult> ResolveAsync(
            string imageReference, string? username, string? password, CancellationToken ct) =>
            Task.FromResult(ReleaseDigestResult.Resolved(ReleaseTestEstate.ApiDigest));
    }

    /// <summary>The teardown's two out-of-process steps, stubbed, so the chain can run it for real.</summary>
    private static readonly Action<IServiceCollection> WithComposeAndProxyDoubles = services => {
        services.Replace(ServiceDescriptor.Singleton<ComposeCliService>(new StubComposeCliService()));
        services.Replace(ServiceDescriptor.Singleton<IProxyProvider>(new RecordingProxyProvider()));
    };

    /// <summary>
    /// Makes the host's *own* deploy queue the recorder, so a coordinator resolved from the container
    /// releases work onto something a test can read.
    /// </summary>
    private static readonly Action<IServiceCollection> WithRecordedDeployQueue = services =>
        services.Replace(ServiceDescriptor.Singleton<DeployQueueService>(RecordingDeployQueue.Create));

    private static async Task<List<BackupEvent>> BackupEventsAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.BackupEvents.AsNoTracking()
            .Where(e => e.StackId == stackId).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    private static (BackupChainCoordinator Coordinator, RecordingDeployQueue Deploys) Coordinator(
        AuthTestHost host) {
        var deploys = RecordingDeployQueue.Create(host);
        return (
            new BackupChainCoordinator(
                deploys,
                host.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackupChainCoordinator>.Instance),
            deploys);
    }

    private static IReadOnlyList<(int StackId, string TriggeredBy, BackupChainStep? Chain)> Chained(
        AuthTestHost host) =>
        ((RecordingBackupQueue)host.Services.GetRequiredService<BackupQueueService>()).Chained;

    private static async Task<Result<RemoveTenant.Response>> RemoveAsync(
        AuthTestHost host, int templateId, string slug, bool finalBackup) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<RemoveTenant>(
            scope.ServiceProvider, host.Services.GetRequiredService<BackupQueueService>());
        return await handler.HandleAsync(
            new RemoveTenant.Command(templateId, slug, RemoveVolumes: false, FinalBackup: finalBackup), Ct);
    }

    private static async Task<bool> StackExistsAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Stacks.AsNoTracking().AnyAsync(s => s.Id == stackId, Ct);
    }

    private static async Task<List<DeployEvent>> DeployEventsAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.DeployEvents.AsNoTracking()
            .Where(e => e.StackId == stackId).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    private static async Task<List<AuditEvent>> AuditAsync(AuthTestHost host, string action) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.Category == BackupService.AuditCategory && e.Action == action)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
