using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Runs a self-update check on startup and then periodically so the "Update available" badge
/// stays fresh without a manual check. Only registered when <c>Watchtower:AutoCheckEnabled</c> is true.
/// </summary>
public sealed class SelfUpdateBackgroundService(
    SelfUpdateService selfUpdate,
    IOptions<WatchtowerOptions> options,
    ILogger<SelfUpdateBackgroundService> logger) : BackgroundService {

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var interval = TimeSpan.FromMinutes(Math.Clamp(options.Value.AutoCheckIntervalMinutes, 1, 1440));

        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested) {
            await RunCheckAsync(interval, stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunCheckAsync(TimeSpan interval, CancellationToken ct) {
        try {
            logger.LogInformation("Background self-update check starting");
            await selfUpdate.CheckForUpdateAsync(ct);
            logger.LogInformation("Background self-update check complete");
        } catch (OperationCanceledException) {
            // Normal shutdown — don't log as an error.
        } catch (Exception ex) {
            logger.LogWarning(ex, "Background self-update check failed; will retry in {Interval}", interval);
        }
    }
}
