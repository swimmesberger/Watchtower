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
/// <remarks>
/// The description above is <c>Git</c> mode, and it is unchanged by ADR-0026. In <c>Releases</c> mode
/// the same three <see cref="AutoDeployMode"/> intents keep their meaning with the mechanism swapped
/// from pull to push: <see cref="AutoDeployMode.OnChange"/> is driven by the release webhook and is
/// skipped here, and <see cref="AutoDeployMode.Scheduled"/> compares the newest release against
/// <see cref="Stack.LastDeployedReleaseId"/> at its window. A pinned stack is skipped in <em>both</em>
/// modes — see <see cref="IsEligible"/>.
/// </remarks>
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
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
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
            if (!IsEligible(stack)) continue;
            switch (stack.AutoDeployMode) {
                case AutoDeployMode.OnChange when IsPollDue(stack.Id, now):
                    _lastPollAt[stack.Id] = now;
                    await EvaluateAsync(stack, DeployTriggers.AutoUpdate, ct);
                    break;
                case AutoDeployMode.Scheduled when IsScheduleDue(stack, now):
                    _lastScheduledDate[stack.Id] = DateOnly.FromDateTime(now.LocalDateTime);
                    await EvaluateAsync(stack, DeployTriggers.Schedule, ct);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether this service may deploy <paramref name="stack"/> at all, before any window or interval
    /// is considered — the two rules <c>Releases</c> mode adds (design.md §"Auto-deploy precedence").
    /// </summary>
    /// <remarks>
    /// The two rules, in the order design.md §"Auto-deploy precedence" gives them:
    /// <list type="number">
    /// <item><b>Rule 2 — a pinned stack never auto-deploys</b>, by any route, <em>in either mode</em>. A
    /// pin is an explicit "stay here", and the precedence list puts it above the mode question on
    /// purpose. The case that decides it is an operator reverting a product to <c>Git</c> mode while
    /// stacks are pinned: reading the pin as release-mode-only would quietly resume branch-head
    /// auto-deploys on exactly the stacks somebody had asked to hold still, and the revert is not where
    /// anyone would look for that. Clearing the pin (<c>stacks.setRelease(null)</c>) is how a stack
    /// rejoins automation, and it works in Git mode for this reason.</item>
    /// <item><b>Rule 3 — an <c>OnChange</c> stack of a <c>Releases</c>-mode product is skipped</b>,
    /// because in that mode its trigger is the release arriving: <see cref="ReleaseRolloutService"/>
    /// enqueues it the moment the webhook accepts, and polling here would race the fan-out to enqueue
    /// the identical convergent deploy. Its badge stays fresh through the separate update-check
    /// schedule.</item>
    /// </list>
    /// Everything else in <c>Git</c> mode is untouched (rule 4).
    /// </remarks>
    internal static bool IsEligible(Stack stack) {
        if (stack.PinnedReleaseId is not null) return false;
        if (!ReleaseResolver.UsesReleases(ReleaseResolver.RequireProduct(stack))) return true;
        return stack.AutoDeployMode != AutoDeployMode.OnChange;
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

    /// <summary>
    /// Runs a full update check — which keeps the UI badge fresh either way — and deploys when it
    /// reports something this mode counts as new.
    /// </summary>
    /// <remarks>
    /// The question differs by mode, which is why <see cref="StackUpdateResult"/> exposes two properties
    /// rather than one: in <c>Git</c> mode a new image digest <em>or</em> a new commit is a reason to
    /// deploy; in <c>Releases</c> mode only a newer release is, because a commit no release was built
    /// from is not something a redeploy would pick up (design.md §"Update checks and drift").
    /// </remarks>
    private async Task EvaluateAsync(Stack stack, string triggeredBy, CancellationToken ct) {
        try {
            var releasesMode = ReleaseResolver.UsesReleases(ReleaseResolver.RequireProduct(stack));
            var result = await stackUpdate.CheckStackAsync(stack, ct);
            if (!(releasesMode ? result.HasNewerRelease : result.HasChanges)) return;

            var reason = releasesMode
                ? $"release {result.AvailableReleaseVersion}"
                : (result.HasUpdates, result.NewCommitSha) switch {
                    (true, not null) => $"new image(s) + commit {result.NewCommitSha[..8]}",
                    (true, null) => $"outdated image(s): {string.Join(", ", result.OutdatedImages)}",
                    (false, var sha) => $"new commit {sha![..8]}",
                };
            logger.LogInformation("Auto-deploying stack {StackName} ({Reason})", stack.Name, reason);
            deployQueue.Enqueue(stack.Id, triggeredBy);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogWarning(ex, "Auto-deploy evaluation failed for stack {StackName}", stack.Name);
        }
    }

    private List<Stack> LoadAutoDeployStacks() {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        // Stopped stacks are deliberately disabled (ADR-0025): no polling, no scheduled deploys.
        return [.. db.Stacks.AsNoTracking()
            // The update check resolves the source from these (ADR-0026).
            .Include(s => s.Product)
            .Include(s => s.Template)
            .Where(s => s.AutoDeployMode != AutoDeployMode.Off && s.DesiredState != StackDesiredState.Stopped)
            .OrderBy(s => s.Name)];
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
