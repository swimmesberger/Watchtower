using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>Returned by the enqueue methods: the event that tracks the run.</summary>
public sealed record BackupEnqueueResult(int BackupEventId, string Status);

/// <summary>
/// Single-flight queue for backup <em>and restore</em> runs (ADR-0016 §6): one runs at a time
/// process-wide — they compete for the same disk, network and daemon, and stopping several stacks at
/// once multiplies the blast radius. Backups coalesce per stack (a stack already waiting is not
/// queued twice; its existing event is returned). Restores never coalesce and are refused outright
/// while the stack has anything queued or running here — a restore interleaving with a backup of the
/// same volumes helps nobody. Each accepted request creates a <c>queued</c>
/// <see cref="BackupEvent"/> up front so the UI can show it immediately, mirroring the deploy queue.
/// </summary>
/// <remarks>
/// Not sealed, and the enqueue methods virtual, for the same reason the deploy queue is: a test host
/// can accept work without spawning the worker against a real Docker daemon.
/// </remarks>
public class BackupQueueService(
    BackupService backupService,
    IServiceScopeFactory scopeFactory,
    ILogger<BackupQueueService> logger) : BackgroundService {

    private enum JobKind { Backup, Restore }

    private sealed record Job(int EventId, int StackId, JobKind Kind, string? FileName);

    private readonly Channel<Job> _channel =
        Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private readonly Dictionary<int, int> _queuedBackupByStack = [];
    private readonly HashSet<int> _queuedRestoreStacks = [];
    private int? _runningStackId;

    /// <summary>
    /// Enqueues a backup for <paramref name="stackId"/>. Returns the tracking event — a fresh
    /// <c>queued</c> one, or the stack's already-waiting backup event (coalesced).
    /// </summary>
    public virtual BackupEnqueueResult Enqueue(int stackId, string triggeredBy) {
        lock (_lock) {
            if (_queuedBackupByStack.TryGetValue(stackId, out var pending))
                return new BackupEnqueueResult(pending, "queued");

            var eventId = CreateEvent(stackId, triggeredBy);
            _queuedBackupByStack[stackId] = eventId;
            _channel.Writer.TryWrite(new Job(eventId, stackId, JobKind.Backup, FileName: null));
            return new BackupEnqueueResult(eventId, "queued");
        }
    }

    /// <summary>
    /// Enqueues a restore of <paramref name="fileName"/> into <paramref name="stackId"/>'s volumes,
    /// or returns null when the stack already has a backup or restore queued or running — the
    /// caller surfaces that as a conflict rather than stacking destructive work.
    /// </summary>
    public virtual BackupEnqueueResult? TryEnqueueRestore(int stackId, string fileName) {
        lock (_lock) {
            var busy = _queuedBackupByStack.ContainsKey(stackId)
                || _queuedRestoreStacks.Contains(stackId)
                || _runningStackId == stackId;
            if (busy) return null;

            var eventId = CreateEvent(stackId, triggeredBy: "restore");
            _queuedRestoreStacks.Add(stackId);
            _channel.Writer.TryWrite(new Job(eventId, stackId, JobKind.Restore, fileName));
            return new BackupEnqueueResult(eventId, "queued");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken)) {
                lock (_lock) {
                    // Only remove the backup mapping if it still points at this event (a newer
                    // request may have been queued for the same stack after this one started).
                    if (job.Kind == JobKind.Backup
                        && _queuedBackupByStack.TryGetValue(job.StackId, out var current)
                        && current == job.EventId)
                        _queuedBackupByStack.Remove(job.StackId);
                    if (job.Kind == JobKind.Restore)
                        _queuedRestoreStacks.Remove(job.StackId);
                    _runningStackId = job.StackId;
                }
                try {
                    if (job.Kind == JobKind.Backup)
                        await backupService.ExecuteBackupAsync(job.EventId, stoppingToken);
                    else
                        await backupService.ExecuteRestoreAsync(job.EventId, job.FileName!, stoppingToken);
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    // The service records its own failures; this catches the truly unexpected.
                    logger.LogError(ex, "Backup worker failed for event {EventId}", job.EventId);
                } finally {
                    lock (_lock) {
                        _runningStackId = null;
                    }
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
