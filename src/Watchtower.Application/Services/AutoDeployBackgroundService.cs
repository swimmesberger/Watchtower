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
/// from pull to push: <see cref="AutoDeployMode.OnChange"/> is driven by the release webhook, and
/// <see cref="AutoDeployMode.Scheduled"/> compares the newest release against
/// <see cref="Stack.LastDeployedReleaseId"/> at its window. The <c>OnChange</c> tick still runs in that
/// mode, but only as the reconcile described on <see cref="ConfirmUnconverged"/>: it contacts no
/// registry and enqueues under <see cref="DeployTriggers.ReleaseReconcile"/>. A pinned stack is skipped
/// in <em>both</em> modes — see <see cref="IsEligible"/>.
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
    private readonly Dictionary<int, int> _unconvergedRelease = [];          // Releases-mode OnChange

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
            var releasesMode = ReleaseResolver.UsesReleases(ReleaseResolver.RequireProduct(stack));
            switch (stack.AutoDeployMode) {
                case AutoDeployMode.OnChange when IsPollDue(stack.Id, now):
                    _lastPollAt[stack.Id] = now;
                    await EvaluateAsync(stack, releasesMode,
                        releasesMode ? DeployTriggers.ReleaseReconcile : DeployTriggers.AutoUpdate, ct);
                    break;
                case AutoDeployMode.Scheduled when IsScheduleDue(stack, now):
                    _lastScheduledDate[stack.Id] = DateOnly.FromDateTime(now.LocalDateTime);
                    await EvaluateAsync(stack, releasesMode, DeployTriggers.Schedule, ct);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether this service may deploy <paramref name="stack"/> at all, before any window, interval
    /// or mode is considered — rule 2 of design.md §"Auto-deploy precedence".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rule 2 — a pinned stack never auto-deploys</b>, by any route, <em>in either mode</em>. A
    /// pin is an explicit "stay here", and the precedence list puts it above the mode question on
    /// purpose. The case that decides it is an operator reverting a product to <c>Git</c> mode while
    /// stacks are pinned: reading the pin as release-mode-only would quietly resume branch-head
    /// auto-deploys on exactly the stacks somebody had asked to hold still, and the revert is not where
    /// anyone would look for that. Clearing the pin (<c>stacks.setRelease(null)</c>) is how a stack
    /// rejoins automation, and it works in Git mode for this reason.
    /// </para>
    /// <para>
    /// <b>Rule 3 no longer gates here</b>, which is the one change to this predicate since ADR-0026.
    /// An <c>OnChange</c> stack of a <c>Releases</c>-mode product used to be refused outright, on the
    /// grounds that the release arriving is its trigger and polling could only race
    /// <see cref="ReleaseRolloutService"/> to the same convergent deploy. True, and beside the point:
    /// that fan-out is one in-process enqueue behind one inbound HTTP call, so when either is lost
    /// there is nothing to race — the stack simply sits on a stale release, indefinitely, with no
    /// surface saying so. Such a stack is now let through as a reconcile rather than refused, and the
    /// race the old rule worried about is what <see cref="ConfirmUnconverged"/> filters out.
    /// </para>
    /// Everything in <c>Git</c> mode is untouched (rule 4).
    /// </remarks>
    internal static bool IsEligible(Stack stack) => stack.PinnedReleaseId is null;

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
    private async Task EvaluateAsync(
        Stack stack, bool releasesMode, string triggeredBy, CancellationToken ct) {
        try {
            var result = await stackUpdate.CheckStackAsync(stack, ct);
            if (!(releasesMode ? result.HasNewerRelease : result.HasChanges)) {
                _unconvergedRelease.Remove(stack.Id);
                return;
            }

            var reconcile = triggeredBy == DeployTriggers.ReleaseReconcile;
            if (reconcile
                && !ConfirmUnconverged(_unconvergedRelease, stack.Id, result.AvailableReleaseId))
                return;

            var reason = releasesMode
                ? $"release {result.AvailableReleaseVersion}"
                : (result.HasUpdates, result.NewCommitSha) switch {
                    (true, not null) => $"new image(s) + commit {result.NewCommitSha[..8]}",
                    (true, null) => $"outdated image(s): {string.Join(", ", result.OutdatedImages)}",
                    (false, var sha) => $"new commit {sha![..8]}",
                };
            if (reconcile) reason += " — the fan-out never delivered it";

            // A reconcile firing means the push path missed one, and this line is the only place that
            // becomes observable — hence a warning, where a fan-out deploy is routine information.
            logger.Log(
                reconcile ? LogLevel.Warning : LogLevel.Information,
                "Auto-deploying stack {StackName} ({Reason})", stack.Name, reason);
            deployQueue.Enqueue(stack.Id, triggeredBy);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogWarning(ex, "Auto-deploy evaluation failed for stack {StackName}", stack.Name);
        }
    }

    /// <summary>
    /// The reconcile's one guard: act only on the second consecutive tick that finds the <em>same</em>
    /// release still undeployed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A release fans out to every eligible stack in a single enqueue, but the cross-stack deploy gate
    /// drains the resulting deploys over minutes. A reconcile tick landing inside that drain finds a
    /// perfectly healthy stack "unconverged" — its deploy is queued, not lost — and would file a
    /// <see cref="DeployTriggers.ReleaseReconcile"/> event against it. The deploy would be harmless: it
    /// coalesces onto the pending one and then short-circuits, because the stack is on the release by
    /// the time it runs (<see cref="DeployTriggers.MayShortCircuit"/>). The <em>event</em> would not be.
    /// A reconcile that also fires during ordinary rollouts tells an operator nothing about whether
    /// their webhooks are arriving, and that is the single question it exists to answer.
    /// </para>
    /// <para>
    /// Requiring the same <see cref="StackUpdateResult.AvailableReleaseId"/> twice, a full check
    /// interval apart, separates the two cases without having to ask the deploy queue what it is busy
    /// with: a drain finishes well inside one interval, whereas a lost enqueue never resolves itself.
    /// The cost is that the safety net's worst case becomes two intervals rather than one — the right
    /// trade for a path that should never fire at all.
    /// </para>
    /// </remarks>
    /// <param name="unconvergedRelease">
    /// <see cref="_unconvergedRelease"/>, passed in rather than closed over so the rule can be tested
    /// without standing up a <see cref="BackgroundService"/> and its five dependencies — the same
    /// reason <see cref="IsEligible"/> is a static.
    /// </param>
    internal static bool ConfirmUnconverged(
        Dictionary<int, int> unconvergedRelease, int stackId, int? availableReleaseId) {
        if (availableReleaseId is not { } releaseId) return false;
        if (unconvergedRelease.TryGetValue(stackId, out var seen) && seen == releaseId) return true;
        unconvergedRelease[stackId] = releaseId;
        return false;
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
        foreach (var id in _unconvergedRelease.Keys.Where(id => !onChange.Contains(id)).ToList())
            _unconvergedRelease.Remove(id);
    }
}
