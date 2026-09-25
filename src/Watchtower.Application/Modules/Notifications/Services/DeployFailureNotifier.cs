using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Modules.Notifications.Contracts;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Notifications.Services;

/// <summary>
/// Turns "deploy N failed" from the deploy engine into a push notification to the operators (ADR-0041).
/// </summary>
/// <remarks>
/// <para>
/// The deploy engine calls <see cref="DeployFailed"/> on its completion path, so that call only drops the
/// id into a bounded in-memory channel and returns; the database reads and the push service round trips
/// happen on this service's own loop. The channel drops the <em>oldest</em> id when full: 256 unsent
/// failures means the push side is badly behind, and the newest failures are the ones still worth
/// hearing about. It is in-memory on purpose — a notification lost to a restart is acceptable for a
/// best-effort hint whose record (the deploy history) is durable anyway.
/// </para>
/// <para>
/// Only the event id travels. The loop loads the trigger and the stack's name itself, skips a stack
/// deleted in the meantime, and applies <see cref="DeployFailureNotificationPolicy"/>'s spam guard against
/// the stack's previous finished deploy. Nothing here throws back into the deploy path, and a failure to
/// notify is logged and forgotten.
/// </para>
/// </remarks>
internal sealed class DeployFailureNotifier(
    IServiceScopeFactory scopeFactory,
    ILogger<DeployFailureNotifier> logger) : BackgroundService, IDeployFailureSink {
    /// <summary>How many failures may wait for the loop before the oldest are dropped.</summary>
    internal const int Capacity = 256;

    private readonly Channel<int> _pending = Channel.CreateBounded<int>(new BoundedChannelOptions(Capacity) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    /// <inheritdoc />
    public void DeployFailed(int deployEventId) {
        // Always succeeds on a DropOldest channel until the host stops and completes it; a failure after
        // that is a shutdown race and not worth a word.
        _pending.Writer.TryWrite(deployEventId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await foreach (var eventId in _pending.Reader.ReadAllAsync(stoppingToken)) {
                try {
                    await NotifyAsync(eventId, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Could not send the failure notification for deploy {EventId}", eventId);
                }
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Shutting down.
        } finally {
            _pending.Writer.TryComplete();
        }
    }

    private async Task NotifyAsync(int eventId, CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        // The stack is joined, not optional: a deploy event's stack cascades, so no row means the stack
        // (and with it the event) was deleted since the failure — nobody needs to hear about it.
        var failed = await db.DeployEvents.AsNoTracking()
            .Where(e => e.Id == eventId && e.Status == "failed")
            .Select(e => new { e.StackId, e.TriggeredBy, StackName = e.Stack!.Name })
            .FirstOrDefaultAsync(ct);
        if (failed is null) return;

        var previous = await db.DeployEvents.AsNoTracking()
            .Where(e => e.StackId == failed.StackId && e.Id < eventId
                        && (e.Status == "success" || e.Status == "failed"))
            .OrderByDescending(e => e.Id)
            .Select(e => e.Status)
            .FirstOrDefaultAsync(ct);
        if (!DeployFailureNotificationPolicy.ShouldNotify(failed.TriggeredBy, previous)) {
            logger.LogDebug(
                "Deploy {EventId} failed again after a failed {Trigger} deploy; not notifying.", eventId, failed.TriggeredBy);
            return;
        }

        var notifier = scope.ServiceProvider.GetRequiredService<IPushNotifier>();
        await notifier.SendToOperatorsAsync(
            DeployFailureNotificationPolicy.Build(failed.StackId, failed.StackName, eventId, failed.TriggeredBy), ct);
    }
}
