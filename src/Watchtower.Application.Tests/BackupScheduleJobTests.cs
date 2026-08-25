using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Backups;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The schedule tick (ADR-0018) against the real registrations: it enqueues exactly the stacks whose
/// window is due, moves each stack's cursor, honours per-stack overrides and the master switch, and
/// stays idempotent across ticks and restarts. The queue is replaced by a recorder — the production
/// queue coalesces a stack that is still waiting, which is right for production and would hide the
/// second window here, where nothing ever drains it.
/// </summary>
public sealed class BackupScheduleJobTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records enqueues instead of queueing them; the worker loop never starts in these tests.</summary>
    private sealed class RecordingBackupQueue(BackupService backupService, IServiceScopeFactory scopeFactory, ILogger<BackupQueueService> logger)
        : BackupQueueService(backupService, scopeFactory, logger) {
        public List<(int StackId, string TriggeredBy)> Enqueued { get; } = [];

        public override BackupEnqueueResult Enqueue(int stackId, string triggeredBy) {
            Enqueued.Add((stackId, triggeredBy));
            return new BackupEnqueueResult(Enqueued.Count, "queued");
        }
    }

    private static AuthTestHost Start(params (string, string?)[] settings) =>
        AuthTestHost.Start(
            services => services.Replace(ServiceDescriptor.Singleton<BackupQueueService, RecordingBackupQueue>()),
            settings);

    private static List<(int StackId, string TriggeredBy)> Enqueued(AuthTestHost host) =>
        ((RecordingBackupQueue)host.Services.GetRequiredService<BackupQueueService>()).Enqueued;

    private static int EnqueuedCount(AuthTestHost host, int stackId) =>
        Enqueued(host).Count(e => e.StackId == stackId && e.TriggeredBy == "schedule");

    private static DateTimeOffset Utc(int day, int hour, int minute, int second = 0) =>
        new(2026, 8, day, hour, minute, second, TimeSpan.Zero);

    private static (string, string?)[] Enabled(params (string, string?)[] more) =>
        [("Watchtower:Backup:Enabled", "true"), ("Watchtower:Backup:Cron", "30 3,15 * * *"), .. more];

    private static async Task<int> AddStackAsync(AuthTestHost host, string name, string? cron = null, DateTimeOffset? last = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = name,
            ComposeProjectName = name,
            Product = TestProducts.New(name),
            BackupEnabled = true,
            BackupCron = cron,
            LastScheduledBackupAt = last,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    private static async Task<int> TickAsync(AuthTestHost host, DateTimeOffset now) {
        await using var scope = host.Services.CreateAsyncScope();
        var job = ActivatorUtilities.CreateInstance<BackupScheduleJob>(scope.ServiceProvider);
        return await job.TickAsync(now, TimeZoneInfo.Utc, Ct);
    }

    private static async Task<DateTimeOffset?> CursorAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Stacks.AsNoTracking().Where(s => s.Id == stackId).Select(s => s.LastScheduledBackupAt).SingleAsync(Ct);
    }

    [Fact]
    public async Task EnqueuesEachStackOnceForAWindowAndMovesTheCursor() {
        using var host = Start(Enabled());
        var web = await AddStackAsync(host, "web", last: Utc(16, 15, 30));
        var api = await AddStackAsync(host, "api", last: Utc(16, 15, 30));

        Assert.Equal(0, await TickAsync(host, Utc(17, 3, 29)));
        Assert.Equal(2, await TickAsync(host, Utc(17, 3, 30, 15)));
        Assert.Equal(0, await TickAsync(host, Utc(17, 3, 31, 15)));   // same window, next tick
        Assert.Equal(0, await TickAsync(host, Utc(17, 12, 0)));

        Assert.Equal(1, EnqueuedCount(host, web));
        Assert.Equal(1, EnqueuedCount(host, api));
        Assert.Equal(Utc(17, 3, 30), await CursorAsync(host, web));

        // The second window of the day.
        Assert.Equal(2, await TickAsync(host, Utc(17, 15, 30, 40)));
        Assert.Equal(2, EnqueuedCount(host, web));
        Assert.Equal(Utc(17, 15, 30), await CursorAsync(host, web));
    }

    [Fact]
    public async Task TheProductionQueueGetsAQueuedEventPerWindow() {
        // One window through the real queue (its worker never starts here): the enqueue is the
        // "queued" event row the UI shows, triggered by "schedule".
        using var host = AuthTestHost.Start(Enabled());
        var web = await AddStackAsync(host, "web", last: Utc(16, 15, 30));
        Assert.Equal(1, await TickAsync(host, Utc(17, 3, 30, 15)));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var evt = await db.BackupEvents.AsNoTracking().SingleAsync(e => e.StackId == web, Ct);
        Assert.Equal("schedule", evt.TriggeredBy);
        Assert.Equal("queued", evt.Status);
    }

    [Fact]
    public async Task ARestartNeitherDoubleFiresNorLosesAWindowWithinTheGrace() {
        using var host = Start(Enabled());
        var web = await AddStackAsync(host, "web", last: Utc(16, 15, 30));
        Assert.Equal(1, await TickAsync(host, Utc(17, 3, 30, 10)));

        // Restart right after the window: the cursor is on disk, so the first tick does nothing.
        using var restarted = host.Restart(Enabled());
        Assert.Equal(0, await TickAsync(restarted, Utc(17, 3, 31)));
        Assert.Equal(0, EnqueuedCount(restarted, web));   // the restarted host has its own (empty) recorder

        // Down across the 15:30 window, back 20 minutes later: that window runs once, late.
        Assert.Equal(1, await TickAsync(restarted, Utc(17, 15, 50)));
        Assert.Equal(Utc(17, 15, 30), await CursorAsync(restarted, web));

        // Down across the next morning's window for longer than the grace: skipped, cursor untouched.
        Assert.Equal(0, await TickAsync(restarted, Utc(18, 6, 0)));
        Assert.Equal(Utc(17, 15, 30), await CursorAsync(restarted, web));
        Assert.Equal(1, EnqueuedCount(restarted, web));
    }

    [Fact]
    public async Task AStackOverrideRunsOnItsOwnSchedule() {
        using var host = Start(Enabled());
        var hourly = await AddStackAsync(host, "hourly", cron: "0 * * * *", last: Utc(17, 3, 0));
        var daily = await AddStackAsync(host, "daily", last: Utc(17, 3, 30));   // this morning's window already ran

        Assert.Equal(1, await TickAsync(host, Utc(17, 4, 0, 5)));       // only the override's 04:00
        Assert.Equal(1, EnqueuedCount(host, hourly));
        Assert.Equal(0, EnqueuedCount(host, daily));
        Assert.Equal(Utc(17, 4, 0), await CursorAsync(host, hourly));
    }

    [Fact]
    public async Task TheMasterSwitchStopsEverything() {
        using var host = Start(("Watchtower:Backup:Enabled", "false"), ("Watchtower:Backup:Cron", "30 3 * * *"));
        var web = await AddStackAsync(host, "web", last: Utc(16, 3, 30));
        Assert.Equal(0, await TickAsync(host, Utc(17, 3, 30, 10)));
        Assert.Empty(Enqueued(host));
        Assert.Equal(Utc(16, 3, 30), await CursorAsync(host, web));
    }

    [Fact]
    public async Task StacksNotOptedInAreLeftAlone() {
        using var host = Start(Enabled());
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var stack = new Stack {
                Name = "quiet", ComposeProjectName = "quiet", BackupEnabled = false,
                Product = TestProducts.New("quiet"),
            };
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(Ct);
        }
        Assert.Equal(0, await TickAsync(host, Utc(17, 3, 30, 10)));
        Assert.Empty(Enqueued(host));
    }

    [Fact]
    public async Task TheLegacyTimeSettingStillSchedulesTheDailyWindow() {
        // An instance upgraded with WATCHTOWER__BACKUP__TIME=04:15 and nothing else keeps its window.
        using var host = Start(("Watchtower:Backup:Enabled", "true"), ("Watchtower:Backup:Time", "04:15"));
        var web = await AddStackAsync(host, "web", last: Utc(16, 4, 15));
        Assert.Equal(0, await TickAsync(host, Utc(17, 3, 30, 10)));
        Assert.Equal(1, await TickAsync(host, Utc(17, 4, 15, 10)));
        Assert.Equal(Utc(17, 4, 15), await CursorAsync(host, web));
    }

    [Fact]
    public async Task AnInvalidInstanceExpressionSchedulesNothingButOverridesStillRun() {
        using var host = Start(("Watchtower:Backup:Enabled", "true"), ("Watchtower:Backup:Cron", "30 99 * * *"));
        var broken = await AddStackAsync(host, "broken", last: Utc(16, 3, 30));
        var own = await AddStackAsync(host, "own", cron: "30 3 * * *", last: Utc(16, 3, 30));
        Assert.Equal(1, await TickAsync(host, Utc(17, 3, 30, 10)));
        Assert.Equal(0, EnqueuedCount(host, broken));
        Assert.Equal(1, EnqueuedCount(host, own));
    }
}
