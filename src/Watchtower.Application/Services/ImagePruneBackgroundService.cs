using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Periodically removes dangling (untagged) images, reclaiming the disk every pull-and-redeploy cycle
/// leaves behind. Always registered; the enabled toggle and interval are read live from
/// <see cref="IOptionsMonitor{WatchtowerOptions}"/> each loop, so they are runtime-editable (via
/// <c>system.updateAutomation</c>) without a restart. When disabled the loop keeps polling on a short
/// cadence but does no work and touches nothing on the host.
/// </summary>
/// <remarks>
/// Only dangling images are pruned (see <see cref="DockerEngineClient.PruneImagesAsync"/>): a tagged
/// image no container happens to be running is still what the next <c>docker compose up</c> without a
/// pull would reuse.
/// </remarks>
public sealed class ImagePruneBackgroundService(
    DockerEngineClient docker,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<ImagePruneBackgroundService> logger) : BackgroundService {

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollWhenDisabled = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(InitialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested) {
                var current = options.CurrentValue;
                var interval = TimeSpan.FromMinutes(Math.Clamp(current.ImagePruneIntervalMinutes, 1, 1440));

                if (current.ImagePruneEnabled) {
                    await RunPruneAsync(interval, stoppingToken);
                    await Task.Delay(interval, stoppingToken);
                } else {
                    // Disabled — do no work, but keep looping so a runtime enable is picked up promptly.
                    await Task.Delay(Min(interval, PollWhenDisabled), stoppingToken);
                }
            }
        } catch (OperationCanceledException) {
            // Normal shutdown — the delay was cancelled. Nothing to log.
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// One prune attempt, reporting the outcome. Internal rather than private so the two failure
    /// branches — a capped prune must be logged, a shutdown must not — can be tested without waiting
    /// out the loop's initial delay.
    /// </summary>
    internal async Task RunPruneAsync(TimeSpan interval, CancellationToken ct) {
        try {
            var result = await docker.PruneImagesAsync(ct);
            if (result.DeletedCount == 0 && result.SpaceReclaimed == 0) {
                logger.LogDebug("Dangling-image prune found nothing to remove");
            } else {
                // Entry count, not image count: the daemon reports an untagged reference and a deleted
                // layer as separate entries, so one image can account for two of them.
                logger.LogInformation(
                    "Dangling-image prune removed {Count} entrie(s), reclaiming {Bytes} bytes",
                    result.DeletedCount, result.SpaceReclaimed);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Normal shutdown — don't log as an error. Guarded on the token because a cancellation
            // for any other reason must reach the warning below rather than vanish. The prune's own
            // ceiling is raised as a TimeoutException, which this filter cannot match: the client
            // decides that from its own cap source, so a shutdown arriving just behind an expired
            // cap is still reported here rather than swallowed.
        } catch (Exception ex) {
            // "Gave up on" rather than "failed": a client-side timeout abandons the request, but the
            // daemon carries the prune through regardless, so the disk may well have been reclaimed.
            logger.LogWarning(ex, "Gave up on the dangling-image prune; will retry in {Interval}", interval);
        }
    }
}
