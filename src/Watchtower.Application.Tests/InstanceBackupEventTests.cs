using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Backups;
using Watchtower.Application.Modules.Backups.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The stackless half of the backup history (ADR-0027): the queue writes an instance run's event with no
/// stack, and the history views can ask for one kind or the other without either becoming a special case.
/// </summary>
public sealed class InstanceBackupEventTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> AddStackAsync(AuthTestHost host, string name) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack { Name = name, ComposeProjectName = name, Product = TestProducts.New(name) };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    private static async Task<IReadOnlyList<BackupEventDto>> EventsAsync(AuthTestHost host, string? kind) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ListBackupEvents>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new ListBackupEvents.Query(Kind: kind), Ct);
        Assert.True(result.IsSuccess);
        return result.Value!.Events;
    }

    [Fact]
    public async Task TheQueueWritesAQueuedEventWithNoStack() {
        using var host = AuthTestHost.Start();
        var queue = host.Services.GetRequiredService<BackupQueueService>();

        var enqueued = queue.EnqueueInstance(BackupTriggers.Manual);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var evt = await db.BackupEvents.AsNoTracking().SingleAsync(e => e.Id == enqueued.BackupEventId, Ct);
        Assert.Null(evt.StackId);
        Assert.Equal("manual", evt.TriggeredBy);
        Assert.Equal("queued", evt.Status);
    }

    [Fact]
    public async Task ASecondRequestCoalescesOntoTheWaitingRun() {
        // Same reason the stack backups coalesce: a caller who asks twice wants the backup that is about
        // to happen, not two of them competing for the disk.
        using var host = AuthTestHost.Start();
        var queue = host.Services.GetRequiredService<BackupQueueService>();

        var first = queue.EnqueueInstance(BackupTriggers.Manual);
        var second = queue.EnqueueInstance(BackupTriggers.Schedule);

        Assert.Equal(first.BackupEventId, second.BackupEventId);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(1, await db.BackupEvents.CountAsync(Ct));
    }

    [Fact]
    public async Task TheHistoryReportsTheKindAndCanBeNarrowedToEither() {
        using var host = AuthTestHost.Start();
        var stackId = await AddStackAsync(host, "web");
        var queue = host.Services.GetRequiredService<BackupQueueService>();
        queue.Enqueue(stackId, BackupTriggers.Manual);
        queue.EnqueueInstance(BackupTriggers.Manual);

        // Unfiltered is unchanged for every existing caller: an instance run is part of "what has this
        // Watchtower been backing up", so it belongs in the instance-wide list.
        var all = await EventsAsync(host, kind: null);
        Assert.Equal(2, all.Count);

        var instance = Assert.Single(await EventsAsync(host, BackupEventKinds.Instance));
        Assert.Equal("instance", instance.Kind);
        Assert.Null(instance.StackId);
        Assert.Null(instance.StackName);

        var stack = Assert.Single(await EventsAsync(host, BackupEventKinds.Stack));
        Assert.Equal("stack", stack.Kind);
        Assert.Equal(stackId, stack.StackId);
        Assert.Equal("web", stack.StackName);
    }

    [Fact]
    public async Task AnUnknownKindIsRefusedRatherThanIgnored() {
        // Silently returning everything would make a typo look like "there are no instance backups".
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ListBackupEvents>(scope.ServiceProvider);

        var result = await handler.HandleAsync(new ListBackupEvents.Query(Kind: "watchtower"), Ct);

        Assert.False(result.IsSuccess);
        Assert.Contains("Kind must be", result.Error!.Message);
    }

    [Fact]
    public async Task DeletingAStackLeavesTheInstanceHistoryStanding() {
        // The relationship still cascades, so a stack takes its own history with it — but the stackless
        // rows outlive every stack, which is the point of keeping them in the same table.
        using var host = AuthTestHost.Start();
        var stackId = await AddStackAsync(host, "web");
        var queue = host.Services.GetRequiredService<BackupQueueService>();
        queue.Enqueue(stackId, BackupTriggers.Manual);
        queue.EnqueueInstance(BackupTriggers.Manual);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.Stacks.Where(s => s.Id == stackId).ExecuteDeleteAsync(Ct);
        }

        var remaining = Assert.Single(await EventsAsync(host, kind: null));
        Assert.Equal("instance", remaining.Kind);
    }
}
