using Elarion.Settings;
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
/// Bringing the stacks back after an instance restore (ADR-0027 §6). The order is the contract: a stack
/// is deployed first, because only a deploy creates the volumes a restore needs, and restored second,
/// because a deploy alone leaves it running on empty ones. Everything else here is about saying clearly
/// which of the two went wrong.
/// </summary>
public sealed class StackRevivalTests : IDisposable {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _storageRoot = Directory.CreateTempSubdirectory("wt-revival-tests").FullName;

    public void Dispose() {
        try {
            Directory.Delete(_storageRoot, recursive: true);
        } catch (IOException) {
            // Scratch space the OS reclaims.
        }
    }

    private AuthTestHost Start() => AuthTestHost.Start(
        ("Watchtower:Backup:Provider", "local"),
        ("Watchtower:Backup:Local:BasePath", _storageRoot),
        ("Watchtower:Backup:InstanceName", "prod"));

    /// <summary>
    /// A coordinator over queues whose runs are already finished. The state machine is what is under
    /// test; how long a real deploy takes is the queues' business, and waiting for one here would only
    /// test <see cref="Task.Delay(TimeSpan)"/>.
    /// </summary>
    private static (StackRevivalCoordinator Coordinator, TerminalDeployQueue Deploys, TerminalBackupQueue Backups)
        Coordinator(AuthTestHost host, string deployStatus = "success", string restoreStatus = "success") {
        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();
        var deploys = new TerminalDeployQueue(host.Services, deployStatus);
        var backups = new TerminalBackupQueue(host.Services, restoreStatus);
        return (
            new StackRevivalCoordinator(
                deploys, backups,
                host.Services.GetRequiredService<BackupStorageFactory>(),
                scopeFactory,
                host.Services.GetRequiredService<IOptionsMonitor<WatchtowerOptions>>(),
                TimeProvider.System,
                NullLogger<StackRevivalCoordinator>.Instance,
                stepTimeout: TimeSpan.FromSeconds(10),
                pollInterval: TimeSpan.FromMilliseconds(5)),
            deploys, backups);
    }

    private static async Task<int> AddStackAsync(AuthTestHost host, string name) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack { Name = name, ComposeProjectName = name, Product = TestProducts.New(name) };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    /// <summary>Seeds the checklist the completion pass would have written.</summary>
    private static async Task SeedChecklistAsync(AuthTestHost host, params (int Id, string Name)[] stacks) {
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        await new StackRevivalState(
            DateTimeOffset.UtcNow, "source", Dismissed: false,
            [.. stacks.Select(s => new RevivalStack(s.Id, s.Name, RevivalStatus.Pending))])
            .SaveAsync(settings, Ct);
    }

    /// <summary>Puts an archive on the storage where the stack's restore would look for it.</summary>
    private async Task<string> SeedArchiveAsync(string stackName) {
        var name = BackupNaming.FileName(
            stackName, new DateTimeOffset(2026, 8, 25, 3, 30, 0, TimeSpan.Zero), encrypted: true);
        var directory = Path.Combine(_storageRoot, "prod", stackName);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name), "archive", Ct);
        return name;
    }

    [Fact]
    public async Task AStackIsDeployedThenRestoredFromItsNewestArchive() {
        using var host = Start();
        var stackId = await AddStackAsync(host, "blog");
        await SeedChecklistAsync(host, (stackId, "blog"));
        var archive = await SeedArchiveAsync("blog");
        var (coordinator, deploys, backups) = Coordinator(host);

        var result = await coordinator.ReviveAsync(stackId, Ct);

        Assert.NotNull(result);
        Assert.Equal(RevivalStatus.Done, result.Status);
        Assert.Contains(archive, result.Detail);
        Assert.Equal([stackId], deploys.Enqueued);
        Assert.Equal([(stackId, archive)], backups.Restored);
        // Both runs are linked from the row, so the operator can read either log.
        Assert.NotNull(result.DeployEventId);
        Assert.NotNull(result.BackupEventId);
    }

    [Fact]
    public async Task AStackWithNoArchiveIsDeployedAndSaysNothingWasRestored() {
        // Its definition came back with the database; its data never existed on the storage. Reporting
        // that as done-with-a-note is honest — reporting it as failed would not be.
        using var host = Start();
        var stackId = await AddStackAsync(host, "fresh");
        await SeedChecklistAsync(host, (stackId, "fresh"));
        var (coordinator, deploys, backups) = Coordinator(host);

        var result = await coordinator.ReviveAsync(stackId, Ct);

        Assert.Equal(RevivalStatus.Done, result!.Status);
        Assert.Contains("nothing was restored", result.Detail);
        Assert.Equal([stackId], deploys.Enqueued);
        Assert.Empty(backups.Restored);
    }

    [Fact]
    public async Task AFailedDeployStopsBeforeTheRestore() {
        // Restoring into volumes a failed deploy never created would either fail confusingly or, worse,
        // succeed against the wrong ones.
        using var host = Start();
        var stackId = await AddStackAsync(host, "blog");
        await SeedChecklistAsync(host, (stackId, "blog"));
        await SeedArchiveAsync("blog");
        var (coordinator, _, backups) = Coordinator(host, deployStatus: "failed");

        var result = await coordinator.ReviveAsync(stackId, Ct);

        Assert.Equal(RevivalStatus.Failed, result!.Status);
        Assert.Contains("The deploy failed", result.Detail);
        Assert.Empty(backups.Restored);
    }

    [Fact]
    public async Task AFailedRestoreIsReportedAsSuch() {
        using var host = Start();
        var stackId = await AddStackAsync(host, "blog");
        await SeedChecklistAsync(host, (stackId, "blog"));
        await SeedArchiveAsync("blog");
        var (coordinator, _, _) = Coordinator(host, restoreStatus: "failed");

        var result = await coordinator.ReviveAsync(stackId, Ct);

        Assert.Equal(RevivalStatus.Failed, result!.Status);
        Assert.Contains("The restore failed", result.Detail);
    }

    [Fact]
    public async Task ReviveAllTakesThePendingAndFailedOnesAndLeavesTheRest() {
        using var host = Start();
        var blog = await AddStackAsync(host, "blog");
        var shop = await AddStackAsync(host, "shop");
        var done = await AddStackAsync(host, "already-done");
        await SeedChecklistAsync(host, (blog, "blog"), (shop, "shop"), (done, "already-done"));

        // Mark one done and one failed, as a half-finished pass would have left them.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
            var checklist = (await StackRevivalState.LoadAsync(settings, Ct))!;
            await checklist
                .With(new RevivalStack(done, "already-done", RevivalStatus.Done, "Deployed."))
                .With(new RevivalStack(shop, "shop", RevivalStatus.Failed, "The deploy failed."))
                .SaveAsync(settings, Ct);
        }

        var (coordinator, deploys, _) = Coordinator(host);
        var revived = await coordinator.ReviveAllAsync(Ct);

        Assert.Equal(2, revived);
        // The pending one and the failed one — a failed stack is exactly what "revive all" should retry.
        Assert.Equal([blog, shop], deploys.Enqueued.Order());
        var checklistAfter = await coordinator.LoadAsync(Ct);
        Assert.All(checklistAfter!.Stacks, s => Assert.Equal(RevivalStatus.Done, s.Status));
    }

    [Fact]
    public async Task ASkippedStackIsLeftAloneByReviveAll() {
        using var host = Start();
        var blog = await AddStackAsync(host, "blog");
        await SeedChecklistAsync(host, (blog, "blog"));
        var (coordinator, deploys, _) = Coordinator(host);

        var skipped = await coordinator.SkipAsync(blog, Ct);
        Assert.Equal(RevivalStatus.Skipped, skipped!.Status);

        Assert.Equal(0, await coordinator.ReviveAllAsync(Ct));
        Assert.Empty(deploys.Enqueued);
    }

    [Fact]
    public async Task DismissingKeepsTheChecklistButStopsOfferingIt() {
        // The record of what happened is the audit trail; this is only the prompt.
        using var host = Start();
        var blog = await AddStackAsync(host, "blog");
        await SeedChecklistAsync(host, (blog, "blog"));
        var (coordinator, _, _) = Coordinator(host);

        await coordinator.DismissAsync(Ct);

        var checklist = await coordinator.LoadAsync(Ct);
        Assert.NotNull(checklist);
        Assert.True(checklist.Dismissed);
    }

    [Fact]
    public async Task AStackThatIsNotOnTheChecklistIsNotRevived() {
        using var host = Start();
        var blog = await AddStackAsync(host, "blog");
        await SeedChecklistAsync(host, (blog, "blog"));
        var (coordinator, deploys, _) = Coordinator(host);

        Assert.Null(await coordinator.ReviveAsync(stackId: 9999, Ct));
        Assert.Empty(deploys.Enqueued);
    }
}

/// <summary>A deploy queue whose runs are already over, with an outcome the test chose.</summary>
internal sealed class TerminalDeployQueue(IServiceProvider services, string status)
    : DeployQueueService(
        services.GetRequiredService<IServiceScopeFactory>(),
        services.GetRequiredService<GitCloneService>(),
        services.GetRequiredService<ComposeCliService>(),
        services.GetRequiredService<DockerEngineClient>(),
        services.GetRequiredService<DeployOutputBroadcaster>(),
        services.GetRequiredService<CaddyManager>(),
        services.GetRequiredService<IOptionsMonitor<WatchtowerOptions>>(),
        NullLogger<DeployQueueService>.Instance) {
    private readonly List<int> _enqueued = [];

    /// <summary>The stacks a deploy was asked for, in call order.</summary>
    public IReadOnlyList<int> Enqueued {
        get { lock (_enqueued) return [.. _enqueued]; }
    }

    public override DeployEnqueueResult Enqueue(
        int stackId, string triggeredBy, IReadOnlyList<string>? removeVolumes = null) {
        using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var deployEvent = new DeployEvent {
            StackId = stackId, TriggeredBy = triggeredBy, Status = status,
            StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
        };
        db.DeployEvents.Add(deployEvent);
        db.SaveChanges();
        lock (_enqueued) _enqueued.Add(stackId);
        return new DeployEnqueueResult(deployEvent.Id, status);
    }
}

/// <summary>A backup queue whose restores are already over, with an outcome the test chose.</summary>
internal sealed class TerminalBackupQueue(IServiceProvider services, string status)
    : BackupQueueService(
        services.GetRequiredService<BackupService>(),
        services.GetRequiredService<InstanceBackupService>(),
        services.GetRequiredService<BackupBundleService>(),
        services.GetRequiredService<BackupChainCoordinator>(),
        services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<BackupQueueService>.Instance) {
    private readonly List<(int StackId, string FileName)> _restored = [];

    /// <summary>Every restore asked for, in call order.</summary>
    public IReadOnlyList<(int StackId, string FileName)> Restored {
        get { lock (_restored) return [.. _restored]; }
    }

    public override BackupEnqueueResult? TryEnqueueRestore(int stackId, string fileName) {
        using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var backupEvent = new BackupEvent {
            StackId = stackId, TriggeredBy = BackupTriggers.Restore, Status = status,
            StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
        };
        db.BackupEvents.Add(backupEvent);
        db.SaveChanges();
        lock (_restored) _restored.Add((stackId, fileName));
        return new BackupEnqueueResult(backupEvent.Id, status);
    }
}
