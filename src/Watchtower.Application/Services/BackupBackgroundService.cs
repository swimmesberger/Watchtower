using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The daily backup scheduler (ADR-0016 §6): once per day, when the server-local clock crosses
/// <c>Backup:Time</c>, every stack with <c>BackupEnabled</c> is enqueued on the single-flight
/// backup queue. Always registered; the master switch and time are read live from
/// <see cref="IOptionsMonitor{WatchtowerOptions}"/> each tick, so they are runtime-editable without
/// a restart. A window that passed while Watchtower was down (or before the feature was enabled) is
/// skipped — same baseline rule as scheduled auto-deploys — so a restart never stops containers
/// outside the configured window.
/// </summary>
public sealed class BackupBackgroundService(
    BackupQueueService queue,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<BackupBackgroundService> logger) : BackgroundService {

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    // Only this service's loop touches this; no locking needed.
    private DateOnly? _lastRunDate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested) {
                try {
                    Tick();
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Backup schedule tick failed; retrying in {Interval}", TickInterval);
                }
                await Task.Delay(TickInterval, stoppingToken);
            }
        } catch (OperationCanceledException) {
            // Normal shutdown.
        }
    }

    private void Tick() {
        var backup = options.CurrentValue.Backup;
        if (!backup.Enabled) {
            // Disabled ⇒ drop the baseline, so enabling later re-baselines instead of firing at once.
            _lastRunDate = null;
            return;
        }

        if (!TimeOnly.TryParseExact(backup.Time, "HH:mm", out var scheduledTime)) {
            logger.LogWarning("Invalid backup time '{Time}' (expected HH:mm); skipping", backup.Time);
            return;
        }

        var now = DateTime.Now; // server-local: Backup:Time is a local wall-clock time
        var today = DateOnly.FromDateTime(now);
        var pastWindow = TimeOnly.FromDateTime(now) >= scheduledTime;

        // First sighting (startup or newly enabled): baseline without firing. If today's window
        // already passed we mark it done, so backups only ever start at the configured time.
        if (_lastRunDate is null) {
            _lastRunDate = pastWindow ? today : today.AddDays(-1);
            return;
        }

        if (!pastWindow || _lastRunDate >= today) return;
        _lastRunDate = today;

        List<(int Id, string Name)> stacks;
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            stacks = db.Stacks.AsNoTracking()
                .Where(s => s.BackupEnabled)
                .OrderBy(s => s.Name)
                .Select(s => new ValueTuple<int, string>(s.Id, s.Name))
                .ToList();
        }
        if (stacks.Count == 0) return;

        logger.LogInformation("Backup window open — enqueuing {Count} stack(s)", stacks.Count);
        foreach (var (id, name) in stacks) {
            var result = queue.Enqueue(id, "schedule");
            logger.LogDebug("Enqueued backup for stack {StackName} (event {EventId})", name, result.BackupEventId);
        }
    }
}
