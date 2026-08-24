using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The startup half of stack desired state (ADR-0025): re-stops every stack an operator deliberately
/// stopped whose containers are running again. Watchtower itself never starts containers outside a
/// deploy, so what this catches is Docker — a <c>restart: always</c> policy revives manually-stopped
/// containers when the daemon restarts (host reboot, engine upgrade), and the stop must win again.
/// </summary>
/// <remarks>
/// Runs once per process start, retrying for a few minutes like the backup pause reconcile
/// (<see cref="BackupQueueService"/>): the Docker daemon regularly comes up after Watchtower on a
/// host reboot — the very moment this reconcile matters most — so giving up on the first connection
/// error would defeat it. Per-stack failures are logged and retried on the next attempt; the loop
/// ends once a whole pass completes without failures.
/// </remarks>
public sealed class StackDesiredStateReconciler(
    IServiceScopeFactory scopeFactory,
    DockerEngineClient docker,
    ComposeCliService compose,
    AuditLog audit,
    ILogger<StackDesiredStateReconciler> logger) : BackgroundService {

    /// <summary>How long to wait between attempts while the daemon (or a stop) keeps failing.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    /// <summary>How many attempts before giving up until the next process start.</summary>
    internal const int Retries = 20; // 15 s × 20 = 5 minutes of tolerance for a slow daemon

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            for (var attempt = 1; attempt <= Retries; attempt++) {
                try {
                    await ReconcileAsync(stoppingToken);
                    return;
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    if (attempt == Retries) {
                        logger.LogError(ex,
                            "Giving up re-stopping deliberately stopped stacks after {Attempts} attempts; they will be reconciled on the next start",
                            Retries);
                        return;
                    }
                    logger.LogWarning(ex,
                        "Could not reconcile stopped stacks (attempt {Attempt}/{Retries}); retrying in {Delay}",
                        attempt, Retries, RetryDelay);
                }
                await Task.Delay(RetryDelay, stoppingToken);
            }
        } catch (OperationCanceledException) {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// One reconcile pass: stops the compose project of every <see cref="StackDesiredState.Stopped"/>
    /// stack that has a container running or restarting. Returns how many stacks were re-stopped.
    /// </summary>
    /// <exception cref="Exception">
    /// Rethrown (first failure) after every stack was attempted, so the caller retries the pass; the
    /// stacks that were already handled become cheap no-ops the second time around.
    /// </exception>
    internal async Task<int> ReconcileAsync(CancellationToken ct) {
        List<(int Id, string Name, string ComposeProjectName)> stopped;
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            stopped = await db.Stacks.AsNoTracking()
                .Where(s => s.DesiredState == StackDesiredState.Stopped)
                .OrderBy(s => s.Id)
                .Select(s => new ValueTuple<int, string, string>(s.Id, s.Name, s.ComposeProjectName))
                .ToListAsync(ct);
        }
        if (stopped.Count == 0) return 0;

        var restopped = new List<string>();
        Exception? failure = null;
        foreach (var stack in stopped) {
            try {
                var containers = await docker.ListContainersByLabelsAsync(
                    [$"{StackLifecycle.ComposeProjectLabel}={stack.ComposeProjectName}"], ct);
                // "restarting" is a restart policy mid-revival — exactly what must be stopped here.
                if (!containers.Any(c =>
                        c.State.Equals("running", StringComparison.OrdinalIgnoreCase) ||
                        c.State.Equals("restarting", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var (exitCode, output) = await compose.StopProjectAsync(stack.ComposeProjectName, ct);
                if (exitCode != 0)
                    throw new InvalidOperationException(
                        $"docker compose stop failed for '{stack.ComposeProjectName}' (exit {exitCode}): {StackLifecycle.Tail(output)}");

                logger.LogWarning(
                    "Re-stopped stack {StackName}: it is deliberately stopped but its containers were running again (Docker restart policy after a daemon restart, most likely)",
                    stack.Name);
                restopped.Add(stack.Name);
            } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
                logger.LogError(ex, "Could not re-stop deliberately stopped stack {StackName}", stack.Name);
                failure ??= ex;
            }
        }

        if (restopped.Count > 0)
            await audit.RecordAsync(StackLifecycle.AuditCategory, "reconcile.stop",
                string.Join(", ", restopped),
                $"re-stopped {restopped.Count} deliberately stopped stack(s) whose containers were running again after a Docker restart",
                ct: CancellationToken.None);
        if (failure is not null) throw failure;
        return restopped.Count;
    }
}
