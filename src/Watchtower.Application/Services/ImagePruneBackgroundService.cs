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
/// Only dangling images are pruned (see <see cref="DockerEngineClient.PruneImagesAsync"/>): an image
/// still carrying a tag is something a stack — or Watchtower's own self-update rollback — may need.
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

    private async Task RunPruneAsync(TimeSpan interval, CancellationToken ct) {
        try {
            var result = await docker.PruneImagesAsync(ct);
            if (result.DeletedCount == 0 && result.SpaceReclaimed == 0) {
                logger.LogDebug("Dangling-image prune found nothing to remove");
            } else {
                logger.LogInformation(
                    "Dangling-image prune removed {Count} image(s), reclaiming {Bytes} bytes",
                    result.DeletedCount, result.SpaceReclaimed);
            }
        } catch (OperationCanceledException) {
            // Normal shutdown — don't log as an error.
        } catch (Exception ex) {
            logger.LogWarning(ex, "Dangling-image prune failed; will retry in {Interval}", interval);
        }
    }
}
