using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Runs a self-update check on startup and then periodically so the "Update available" badge
/// stays fresh without a manual check. Always registered; the enabled toggle and interval are read
/// live from <see cref="IOptionsMonitor{WatchtowerOptions}"/> each loop, so they are runtime-editable
/// (via <c>system.updateAutomation</c>) without a restart. When disabled the loop keeps polling on a
/// short cadence but does no work and generates no outbound registry traffic.
/// </summary>
public sealed class SelfUpdateBackgroundService(
    SelfUpdateService selfUpdate,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<SelfUpdateBackgroundService> logger) : BackgroundService {

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollWhenDisabled = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(InitialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested) {
                var current = options.CurrentValue;
                var interval = TimeSpan.FromMinutes(Math.Clamp(current.AutoCheckIntervalMinutes, 1, 1440));

                if (current.AutoCheckEnabled) {
                    await RunCheckAsync(interval, stoppingToken);
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
