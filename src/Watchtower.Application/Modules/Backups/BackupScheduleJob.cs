using System.Collections.Concurrent;
using System.Globalization;
using Elarion.Abstractions.Scheduling;
using Elarion.Settings;
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
    ISettingsManager settings,
    IOptionsMonitor<WatchtowerOptions> options,
    TimeProvider timeProvider,
    ILogger<BackupScheduleJob> logger) {
    /// <summary>The stable job name (logs, telemetry, inspection).</summary>
    public const string JobName = "backups.schedule";

    // Missed windows are reported once per (stack, window) for the life of the process; the cursor does
    // not move for a skip, so without this the same skip would be logged every minute until the next run.
    private static readonly ConcurrentDictionary<int, DateTimeOffset> LoggedMisses = new();

    /// <summary>The instance self-backup's missed window, deduplicated like <see cref="LoggedMisses"/>.</summary>
    private static DateTimeOffset? _loggedInstanceMiss;

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

        // Watchtower's own database runs on the instance-wide expression — there is one instance, so
        // there is nothing to override it with (ADR-0027).
        var enqueuedInstance = globalCron is null
            ? 0
            : await TickInstanceAsync(backup, globalCron, now, grace, timeZone, ct);

        // The ladder in SQL: a stack that says yes, or a tenant that says nothing over a template that
        // says yes. Kept as a predicate rather than "load everything and resolve in memory" because this
        // runs once a minute against every stack on the box. `BackupPolicyResolver` is still the only
        // thing that decides — the query narrows, and the resolver below confirms.
        var stacks = await db.Stacks
            .Include(s => s.Template)
            .Where(s => s.BackupEnabled == true
                || (s.BackupEnabled == null && s.Template != null && s.Template.BackupEnabled == true))
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        if (stacks.Count == 0) return enqueuedInstance;

        var enqueued = enqueuedInstance;
        foreach (var stack in stacks) {
            var policy = BackupPolicyResolver.Resolve(stack, stack.Template);
            if (!policy.Enabled) continue;
            CronExpression? cron = globalCron;
            if (policy.Cron is { } expression) {
                if (BackupSchedule.TryParse(expression, out var own, out var ownError)) cron = own;
                else logger.LogWarning(
                    "Invalid backup schedule override ({Source}) for stack {StackName}: {Error}; using the instance schedule",
                    policy.CronSource, stack.Name, ownError);
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
            var result = queue.Enqueue(stack.Id, BackupTriggers.Schedule);
            stack.LastScheduledBackupAt = due;
            enqueued++;
            logger.LogInformation(
                "Backup window {Window:o} open — enqueued stack {StackName} (event {EventId})",
                due, stack.Name, result.BackupEventId);
        }

        if (enqueued > 0) await db.SaveChangesAsync(ct);
        return enqueued;
    }

    /// <summary>
    /// The same window evaluation for Watchtower's own database (ADR-0027). Returns 1 when it enqueued.
    /// </summary>
    /// <remarks>
    /// The cursor is a settings row rather than a column, because there is no instance table to put it on
    /// — <see cref="WatchtowerSettingPaths.BackupSelfLastScheduledAt"/>, read through the settings manager
    /// rather than the options monitor so a value written last tick is certainly seen this tick (the
    /// configuration snapshot reloads asynchronously, and a stale cursor would fire the window twice).
    /// </remarks>
    private async ValueTask<int> TickInstanceAsync(
        BackupOptions backup, CronExpression cron, DateTimeOffset now, TimeSpan grace,
        TimeZoneInfo timeZone, CancellationToken ct) {
        if (!backup.IncludeSelf) return 0;

        var cursor = ParseCursor(await settings.GetStringAsync(
            WatchtowerSettingPaths.BackupSelfLastScheduledAt, SettingsScope.Global, ct));

        ScheduleDecision decision;
        try {
            decision = BackupSchedule.Evaluate(cron, now, cursor, grace, timeZone);
        } catch (InvalidOperationException ex) {
            logger.LogWarning(ex, "The backup schedule has no upcoming window for Watchtower's own database");
            return 0;
        }

        if (decision.MissedAt is { } missed && _loggedInstanceMiss != missed) {
            _loggedInstanceMiss = missed;
            logger.LogInformation(
                "Backup window {Window:o} for Watchtower's own database was missed (older than the {Grace} "
                + "misfire grace); skipped", missed, grace);
        }

        if (decision.DueAt is not { } due) return 0;

        // Refused rather than run: without a passphrase the run would fail every night and fill the
        // history with failures, and the dump it would have written carries every role's password hash.
        // The cursor still moves — a window that was evaluated is a window that is over, and leaving it
        // open would re-fire it (and re-log) on every tick until the passphrase appears.
        await StoreCursorAsync(due, ct);
        if (string.IsNullOrEmpty(backup.EncryptionPassphrase)) {
            logger.LogWarning(
                "Backup window {Window:o} open for Watchtower's own database, but no encryption passphrase "
                + "is configured — skipping. Set one under Settings → Backups.", due);
            return 0;
        }

        var result = queue.EnqueueInstance(BackupTriggers.Schedule);
        logger.LogInformation(
            "Backup window {Window:o} open — enqueued Watchtower's own database (event {EventId})",
            due, result.BackupEventId);
        return 1;
    }

    /// <summary>Writes the instance cursor round-trip formatted, so it parses back exactly.</summary>
    private async ValueTask StoreCursorAsync(DateTimeOffset due, CancellationToken ct) =>
        await settings.SetStringAsync(
            WatchtowerSettingPaths.BackupSelfLastScheduledAt, due.UtcDateTime.ToString("O"),
            SettingsScope.Global, expectedVersion: null, ct);

    /// <summary>
    /// The stored cursor, or null when there is none — or when it is unreadable, which is treated as
    /// "never ran": the misfire grace then bounds how far back the first window can be.
    /// </summary>
    private static DateTimeOffset? ParseCursor(string? stored) =>
        DateTimeOffset.TryParse(
            stored, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var cursor)
            ? cursor
            : null;
}
