using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Pull-based deployment: redeploys stacks without an inbound webhook by polling for changes
/// (newer image digests in the registry, new commits on the tracked git branch).
///
/// Ticks once per minute and evaluates each stack whose <see cref="Stack.AutoDeployMode"/> is not
/// <see cref="AutoDeployMode.Off"/>:
/// <list type="bullet">
///   <item><description>
///     <see cref="AutoDeployMode.OnChange"/> — checked every <c>StackCheckIntervalMinutes</c>
///     (the same runtime-editable knob the badge checker uses); a detected change deploys immediately.
///   </description></item>
///   <item><description>
///     <see cref="AutoDeployMode.Scheduled"/> — checked once per day when the server-local clock
///     crosses <see cref="Stack.AutoDeployTime"/> (e.g. "02:00"); deploys only if something new is
///     available. A window that passed while Watchtower was down or before the stack was configured
///     is skipped, so a restart never deploys outside the maintenance window.
///   </description></item>
/// </list>
/// Every evaluation runs a full <see cref="StackUpdateService"/> check, so the UI badge stays fresh
/// as a side effect. Deploys go through <see cref="DeployQueueService"/> and coalesce as usual.
/// </summary>
public sealed class AutoDeployBackgroundService(
    StackUpdateService stackUpdate,
    DeployQueueService deployQueue,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<AutoDeployBackgroundService> logger) : BackgroundService {

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    // Only this service's loop touches these; no locking needed.
    private readonly Dictionary<int, DateTimeOffset> _lastPollAt = [];       // OnChange stacks
    private readonly Dictionary<int, DateOnly> _lastScheduledDate = [];      // Scheduled stacks

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await TickAsync(stoppingToken);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Auto-deploy tick failed; retrying in {Interval}", TickInterval);
                }
                await Task.Delay(TickInterval, stoppingToken);
            }
        } catch (OperationCanceledException) {
            // Normal shutdown.
        }
    }

    private async Task TickAsync(CancellationToken ct) {
        var stacks = LoadAutoDeployStacks();
        PruneState(stacks);
        if (stacks.Count == 0) return;

        var now = DateTimeOffset.Now; // server-local: AutoDeployTime is a local wall-clock time
        foreach (var stack in stacks) {
            if (ct.IsCancellationRequested) break;
            switch (stack.AutoDeployMode) {
                case AutoDeployMode.OnChange when IsPollDue(stack.Id, now):
                    _lastPollAt[stack.Id] = now;
                    await EvaluateAsync(stack, triggeredBy: "auto-update", ct);
                    break;
                case AutoDeployMode.Scheduled when IsScheduleDue(stack, now):
                    _lastScheduledDate[stack.Id] = DateOnly.FromDateTime(now.LocalDateTime);
                    await EvaluateAsync(stack, triggeredBy: "schedule", ct);
                    break;
            }
        }
    }

    private bool IsPollDue(int stackId, DateTimeOffset now) {
        var interval = TimeSpan.FromMinutes(Math.Clamp(options.CurrentValue.StackCheckIntervalMinutes, 1, 1440));
        return !_lastPollAt.TryGetValue(stackId, out var last) || now - last >= interval;
    }

    private bool IsScheduleDue(Stack stack, DateTimeOffset now) {
        if (!TimeOnly.TryParseExact(stack.AutoDeployTime, "HH:mm", out var scheduledTime)) {
            logger.LogWarning(
                "Stack {StackName} has an invalid auto-deploy time '{Time}'; skipping",
                stack.Name, stack.AutoDeployTime);
            return false;
        }

        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var pastWindow = TimeOnly.FromDateTime(now.LocalDateTime) >= scheduledTime;

        // First sighting (startup or newly configured): baseline without firing. If today's window
        // already passed we mark it done, so the deploy only ever runs at the configured time.
        if (!_lastScheduledDate.TryGetValue(stack.Id, out var lastRun)) {
            _lastScheduledDate[stack.Id] = pastWindow ? today : today.AddDays(-1);
            return false;
        }

        return pastWindow && lastRun < today;
    }

    private async Task EvaluateAsync(Stack stack, string triggeredBy, CancellationToken ct) {
        try {
            var result = await stackUpdate.CheckStackAsync(stack, ct);
            if (!result.HasChanges) return;

            var reason = (result.HasUpdates, result.NewCommitSha) switch {
                (true, not null) => $"new image(s) + commit {result.NewCommitSha[..8]}",
                (true, null) => $"outdated image(s): {string.Join(", ", result.OutdatedImages)}",
                (false, var sha) => $"new commit {sha![..8]}",
            };
            logger.LogInformation("Auto-deploying stack {StackName} ({Reason})", stack.Name, reason);
            deployQueue.Enqueue(stack.Id, triggeredBy);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            logger.LogWarning(ex, "Auto-deploy evaluation failed for stack {StackName}", stack.Name);
        }
    }

    private List<Stack> LoadAutoDeployStacks() {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return [.. db.Stacks.AsNoTracking().Where(s => s.AutoDeployMode != AutoDeployMode.Off).OrderBy(s => s.Name)];
    }

    /// <summary>Drops tracking state for stacks that were deleted or whose mode changed.</summary>
    private void PruneState(List<Stack> stacks) {
        var onChange = stacks.Where(s => s.AutoDeployMode == AutoDeployMode.OnChange).Select(s => s.Id).ToHashSet();
        var scheduled = stacks.Where(s => s.AutoDeployMode == AutoDeployMode.Scheduled).Select(s => s.Id).ToHashSet();
        foreach (var id in _lastPollAt.Keys.Where(id => !onChange.Contains(id)).ToList())
            _lastPollAt.Remove(id);
        foreach (var id in _lastScheduledDate.Keys.Where(id => !scheduled.Contains(id)).ToList())
            _lastScheduledDate.Remove(id);
    }
}
