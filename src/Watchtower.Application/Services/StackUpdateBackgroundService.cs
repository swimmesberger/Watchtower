using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Periodically checks whether any container image in each stack has a newer version available.
/// Results are cached so the UI badge stays fresh. Only registered when <c>Watchtower:StackCheckEnabled</c> is true.
/// </summary>
public sealed class StackUpdateBackgroundService(
    StackUpdateService stackUpdate,
    IOptions<WatchtowerOptions> options,
    ILogger<StackUpdateBackgroundService> logger) : BackgroundService {

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var interval = TimeSpan.FromMinutes(Math.Clamp(options.Value.StackCheckIntervalMinutes, 1, 1440));

        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested) {
            await RunCheckAsync(interval, stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunCheckAsync(TimeSpan interval, CancellationToken ct) {
        try {
            logger.LogInformation("Background stack update check starting");
            await stackUpdate.CheckAllStacksAsync(ct);
            logger.LogInformation("Background stack update check complete");
        } catch (OperationCanceledException) {
            // Normal shutdown — don't log as an error.
        } catch (Exception ex) {
            logger.LogWarning(ex, "Background stack update check failed; will retry in {Interval}", interval);
        }
    }
}
