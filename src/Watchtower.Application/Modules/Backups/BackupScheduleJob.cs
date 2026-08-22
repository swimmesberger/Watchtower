using System.Collections.Concurrent;
using Elarion.Abstractions.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups;

/// <summary>
/// The backup schedule's minute tick (ADR-0018): an Elarion <c>[ScheduledJob]</c> that evaluates every
/// opted-in stack's effective cron expression against the server-local clock and enqueues the stacks
/// whose window is due on the single-flight backup queue. Per-stack expressions cannot be registered
/// as individual scheduler jobs (they live in the database and change at runtime), so one fixed-rate
/// tick does the evaluation with <see cref="BackupSchedule"/> — the master switch, the instance-wide
/// expression and the misfire grace are read live from the options monitor on every tick.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Entities.Stack.LastScheduledBackupAt"/> is the cursor: the due time of the last window
/// the tick enqueued for the stack. It is persisted, so a restart neither fires a window a second time
/// nor loses one that opened while Watchtower was down — instead the misfire policy applies: the
/// latest late window runs once if it is younger than <c>Backup:MisfireGraceMinutes</c>, older windows
/// are skipped and logged (once per window while the process lives). Manual runs never move the cursor.
/// </para>
/// <para>
/// Scoped (the scheduler runs each occurrence in its own DI scope), so the context is the ordinary
/// scoped one. <c>Overlap = Skip</c> means a slow tick is never doubled up.
/// </para>
/// </remarks>
public sealed class BackupScheduleJob(
    WatchtowerDbContext db,
    BackupQueueService queue,
    IOptionsMonitor<WatchtowerOptions> options,
    TimeProvider timeProvider,
    ILogger<BackupScheduleJob> logger) {
    /// <summary>The stable job name (logs, telemetry, inspection).</summary>
    public const string JobName = "backups.schedule";

    // Missed windows are reported once per (stack, window) for the life of the process; the cursor does
    // not move for a skip, so without this the same skip would be logged every minute until the next run.
    private static readonly ConcurrentDictionary<int, DateTimeOffset> LoggedMisses = new();

    [ScheduledJob(JobName, FixedRate = "1m", Overlap = ScheduledJobOverlap.Skip)]
    public async ValueTask RunAsync(CancellationToken ct) =>
        await TickAsync(timeProvider.GetUtcNow(), TimeZoneInfo.Local, ct);

    /// <summary>
    /// One evaluation pass at <paramref name="now"/>, with the expressions read as wall-clock time in
    /// <paramref name="timeZone"/>. Returns how many stacks were enqueued. Public so tests can drive it
    /// with a fixed clock and zone instead of waiting for the scheduler.
    /// </summary>
    public async ValueTask<int> TickAsync(DateTimeOffset now, TimeZoneInfo timeZone, CancellationToken ct) {
        var backup = options.CurrentValue.Backup;
        if (!backup.Enabled) return 0;

        var globalExpression = BackupSchedule.ResolveGlobalExpression(backup);
        if (!BackupSchedule.TryParse(globalExpression, out var globalCron, out var globalError)) {
            // Only reachable through an env var (the handler validates the stored value); keep saying so.
            logger.LogWarning("Invalid backup schedule {Expression}: {Error}", globalExpression, globalError);
            globalCron = null;
        }
        var grace = BackupSchedule.ResolveMisfireGrace(backup);

        var stacks = await db.Stacks
            .Where(s => s.BackupEnabled)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        if (stacks.Count == 0) return 0;

        var enqueued = 0;
        foreach (var stack in stacks) {
            CronExpression? cron = globalCron;
            if (stack.BackupCron is not null) {
                if (BackupSchedule.TryParse(stack.BackupCron, out var own, out var ownError)) cron = own;
                else logger.LogWarning("Invalid backup schedule override on stack {StackName}: {Error}; using the instance schedule", stack.Name, ownError);
            }
            if (cron is null) continue;

            ScheduleDecision decision;
            try {
                decision = BackupSchedule.Evaluate(cron, now, stack.LastScheduledBackupAt, grace, timeZone);
            } catch (InvalidOperationException ex) {
                // An expression with no occurrence in the next five years — TryParse rejects those, so
                // this is belt and braces against an edge the probe did not reach.
                logger.LogWarning(ex, "Backup schedule for stack {StackName} has no upcoming window", stack.Name);
                continue;
            }

            if (decision.MissedAt is { } missed
                && (!LoggedMisses.TryGetValue(stack.Id, out var logged) || logged != missed)) {
                LoggedMisses[stack.Id] = missed;
                logger.LogInformation(
                    "Backup window {Window:o} for stack {StackName} was missed (older than the {Grace} misfire grace); skipped",
                    missed, stack.Name, grace);
            }

            if (decision.DueAt is not { } due) continue;
            var result = queue.Enqueue(stack.Id, "schedule");
            stack.LastScheduledBackupAt = due;
            enqueued++;
            logger.LogInformation(
                "Backup window {Window:o} open — enqueued stack {StackName} (event {EventId})",
                due, stack.Name, result.BackupEventId);
        }

        if (enqueued > 0) await db.SaveChangesAsync(ct);
        return enqueued;
    }
}
