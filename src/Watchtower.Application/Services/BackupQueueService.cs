using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>Returned by <see cref="BackupQueueService.Enqueue"/>: the event that tracks the run.</summary>
public sealed record BackupEnqueueResult(int BackupEventId, string Status);

/// <summary>
/// Single-flight backup queue (ADR-0016 §6): one backup runs at a time process-wide — backups
/// compete for the same disk, network and daemon, and stopping several stacks at once multiplies the
/// blast radius — with per-stack coalescing (a stack already waiting is not queued twice; its
/// existing event is returned). Each accepted request creates a <c>queued</c>
/// <see cref="BackupEvent"/> up front so the UI can show it immediately, mirroring the deploy queue.
/// </summary>
/// <remarks>
/// Not sealed, and <see cref="Enqueue"/> virtual, for the same reason the deploy queue is: a test
/// host can accept work without spawning the worker against a real Docker daemon.
/// </remarks>
public class BackupQueueService(
    BackupService backupService,
    IServiceScopeFactory scopeFactory,
    ILogger<BackupQueueService> logger) : BackgroundService {

    private readonly Channel<(int EventId, int StackId)> _channel =
        Channel.CreateUnbounded<(int, int)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private readonly Dictionary<int, int> _queuedEventByStack = [];

    /// <summary>
    /// Enqueues a backup for <paramref name="stackId"/>. Returns the tracking event — a fresh
    /// <c>queued</c> one, or the stack's already-waiting event (coalesced).
    /// </summary>
    public virtual BackupEnqueueResult Enqueue(int stackId, string triggeredBy) {
        lock (_lock) {
            if (_queuedEventByStack.TryGetValue(stackId, out var pending))
                return new BackupEnqueueResult(pending, "queued");

            var eventId = CreateEvent(stackId, triggeredBy);
            _queuedEventByStack[stackId] = eventId;
            _channel.Writer.TryWrite((eventId, stackId));
            return new BackupEnqueueResult(eventId, "queued");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await foreach (var (eventId, stackId) in _channel.Reader.ReadAllAsync(stoppingToken)) {
                lock (_lock) {
                    // Only remove the mapping if it still points at this event (a newer request may
                    // have been queued for the same stack after this one started its journey).
                    if (_queuedEventByStack.TryGetValue(stackId, out var current) && current == eventId)
                        _queuedEventByStack.Remove(stackId);
                }
                try {
                    await backupService.ExecuteBackupAsync(eventId, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    // ExecuteBackupAsync records its own failures; this catches the truly unexpected.
                    logger.LogError(ex, "Backup worker failed for event {EventId}", eventId);
                }
            }
        } catch (OperationCanceledException) {
            // Normal shutdown; interrupted events are swept to 'failed' on the next start.
        }
    }

    private int CreateEvent(int stackId, string triggeredBy) {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var evt = new BackupEvent {
            StackId = stackId,
            TriggeredBy = triggeredBy,
            Status = "queued",
            StartedAt = DateTimeOffset.UtcNow,
        };
        db.BackupEvents.Add(evt);
        db.SaveChanges();
        return evt.Id;
    }
}
