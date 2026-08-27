using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
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
    InstanceBackupService instanceBackupService,
    BackupBundleService bundleService,
    BackupChainCoordinator chain,
    IServiceScopeFactory scopeFactory,
    ILogger<BackupQueueService> logger) : BackgroundService {

    private enum JobKind { Backup, Restore, InstanceBackup, BundleExport }

    /// <summary><see cref="Job.StackId"/> is null for the jobs that back up Watchtower itself (ADR-0027).</summary>
    private sealed record Job(int EventId, int? StackId, JobKind Kind, string? FileName);

    private readonly Channel<Job> _channel =
        Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private readonly Dictionary<int, int> _queuedBackupByStack = [];
    private readonly HashSet<int> _queuedRestoreStacks = [];
    private int? _runningStackId;

    /// <summary>
    /// The instance self-backup waiting on the queue, if any — the stackless counterpart of
    /// <see cref="_queuedBackupByStack"/>, coalescing for the same reason: a second request while one is
    /// still waiting wants the backup that is about to happen, not two of them.
    /// </summary>
    private int? _queuedInstanceEventId;

    /// <summary>
    /// The bundle export waiting on the queue, if any. Coalesced like the others: an export takes a
    /// fresh dump and downloads every stack's newest archive, so two of them are minutes of duplicated
    /// work for one file that only the second would keep.
    /// </summary>
    private int? _queuedBundleEventId;

    /// <summary>
    /// Enqueues a backup for <paramref name="stackId"/>. Returns the tracking event — a fresh
    /// <c>queued</c> one, or the stack's already-waiting backup event (coalesced).
    /// </summary>
    /// <param name="stackId">The stack to back up.</param>
    /// <param name="triggeredBy">What to record on the event — see <see cref="BackupTriggers"/>.</param>
    /// <param name="chainStep">
    /// Optional work to run once (and only if) this backup succeeds — the pre-deploy and final-backup
    /// chains of design.md §"Backups across tenants". Registered <em>inside</em> the lock and before the
    /// job is written to the channel, so a run that fails in milliseconds cannot finish before its
    /// follow-up is attached. A coalesced request attaches to the pending event, which is exactly right:
    /// one backup, both follow-ups.
    /// </param>
    public virtual BackupEnqueueResult Enqueue(
        int stackId, string triggeredBy, BackupChainStep? chainStep = null) {
        lock (_lock) {
            if (_queuedBackupByStack.TryGetValue(stackId, out var pending)) {
                if (chainStep is not null) chain.Attach(pending, chainStep);
                return new BackupEnqueueResult(pending, "queued");
            }

            var eventId = CreateEvent(stackId, triggeredBy);
            if (chainStep is not null) chain.Attach(eventId, chainStep);
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

            var eventId = CreateEvent(stackId, BackupTriggers.Restore);
            _queuedRestoreStacks.Add(stackId);
            _channel.Writer.TryWrite(new Job(eventId, stackId, JobKind.Restore, fileName));
            return new BackupEnqueueResult(eventId, "queued");
        }
    }

    /// <summary>
    /// Enqueues a backup of Watchtower's own database (ADR-0027). Returns the tracking event — a fresh
    /// stackless <c>queued</c> one, or the already-waiting instance backup (coalesced).
    /// </summary>
    /// <remarks>
    /// Deliberately on the same single-flight queue as the stack runs rather than beside it: they compete
    /// for the same spool disk, the same storage connection and the same daemon, and the instance dump is
    /// small and infrequent. The cost is that it waits behind a large stack backup, which is the right way
    /// round — a queued dump is a delayed dump, whereas two runs racing for the disk is a failed one.
    /// </remarks>
    /// <param name="triggeredBy">What to record on the event — see <see cref="BackupTriggers"/>.</param>
    public virtual BackupEnqueueResult EnqueueInstance(string triggeredBy) {
        lock (_lock) {
            if (_queuedInstanceEventId is { } pending) return new BackupEnqueueResult(pending, "queued");

            var eventId = CreateEvent(stackId: null, triggeredBy);
            _queuedInstanceEventId = eventId;
            _channel.Writer.TryWrite(new Job(eventId, StackId: null, JobKind.InstanceBackup, FileName: null));
            return new BackupEnqueueResult(eventId, "queued");
        }
    }

    /// <summary>
    /// Enqueues a full backup bundle export (ADR-0027 §4) — a fresh instance dump plus every stack's
    /// newest archive, staged on disk for download. Returns the tracking event, coalescing onto an
    /// export that is already waiting.
    /// </summary>
    /// <param name="triggeredBy">What to record on the event — see <see cref="BackupTriggers"/>.</param>
    public virtual BackupEnqueueResult EnqueueBundleExport(string triggeredBy) {
        lock (_lock) {
            if (_queuedBundleEventId is { } pending) return new BackupEnqueueResult(pending, "queued");

            var eventId = CreateEvent(stackId: null, triggeredBy);
            _queuedBundleEventId = eventId;
            _channel.Writer.TryWrite(new Job(eventId, StackId: null, JobKind.BundleExport, FileName: null));
            return new BackupEnqueueResult(eventId, "queued");
        }
    }

    /// <summary>How often the startup reconcile retries while the daemon is not answering.</summary>
    internal static readonly TimeSpan ReconcileRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>How many times the startup reconcile retries before handing over to the per-job attempt.</summary>
    internal const int ReconcileRetries = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            // A previous process may have died inside a pause window (ADR-0019): thaw its containers
            // before anything else, retrying while the daemon is still coming up. After that every job
            // re-checks once more — cheap when the table is empty, and it closes the gap if the daemon
            // was unreachable for the whole startup budget.
            for (var attempt = 1; !await TryUnpauseLeftoversAsync(stoppingToken) && attempt < ReconcileRetries; attempt++)
                await Task.Delay(ReconcileRetryDelay, stoppingToken);

            await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken)) {
                await TryUnpauseLeftoversAsync(stoppingToken);
                lock (_lock) {
                    // Only remove the backup mapping if it still points at this event (a newer
                    // request may have been queued for the same stack after this one started).
                    if (job.Kind == JobKind.Backup && job.StackId is { } backupStack
                        && _queuedBackupByStack.TryGetValue(backupStack, out var current)
                        && current == job.EventId)
                        _queuedBackupByStack.Remove(backupStack);
                    if (job.Kind == JobKind.Restore && job.StackId is { } restoreStack)
                        _queuedRestoreStacks.Remove(restoreStack);
                    if (job.Kind == JobKind.InstanceBackup && _queuedInstanceEventId == job.EventId)
                        _queuedInstanceEventId = null;
                    if (job.Kind == JobKind.BundleExport && _queuedBundleEventId == job.EventId)
                        _queuedBundleEventId = null;
                    _runningStackId = job.StackId;
                }
                try {
                    if (job.Kind == JobKind.BundleExport)
                        await bundleService.ExecuteExportAsync(job.EventId, stoppingToken);
                    else if (job.Kind == JobKind.InstanceBackup)
                        await instanceBackupService.ExecuteInstanceBackupAsync(job.EventId, stoppingToken);
                    else if (job.Kind == JobKind.Backup)
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
                    // Whatever was chained to this backup runs (or is refused) here, from the stored
                    // outcome rather than from whether the call above threw: BackupService catches its
                    // own failures and records them on the event, so the event is the only honest source
                    // of "did it work". Restores are never chained, and a missing event answers false —
                    // the stack was deleted mid-run, so a follow-up would have nothing to act on.
                    if (job.Kind == JobKind.Backup)
                        await NotifyChainAsync(job.EventId, stoppingToken);
                }
            }
        } catch (OperationCanceledException) {
            // Normal shutdown; interrupted events are swept to 'failed' on the next start.
        }
    }

    /// <summary>
    /// One attempt at <see cref="BackupService.UnpauseLeftoversAsync"/>; false when the engine could not
    /// be reached, which is the only reason to try again — the table is already empty otherwise.
    /// </summary>
    private async Task<bool> TryUnpauseLeftoversAsync(CancellationToken ct) {
        try {
            await backupService.UnpauseLeftoversAsync(ct);
            return true;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogError(ex, "Could not reconcile containers left paused by an interrupted backup; will retry");
            return false;
        }
    }

    /// <summary>
    /// Reads the terminal status the run recorded and lets <see cref="BackupChainCoordinator"/> release
    /// (or refuse) whatever was chained to it.
    /// </summary>
    /// <remarks>
    /// Never allowed to take the worker loop down: a chain that throws must not stop the queue draining,
    /// so the coordinator's own per-step catch is backed by this one.
    /// </remarks>
    private async Task NotifyChainAsync(int eventId, CancellationToken ct) {
        try {
            bool success;
            using (var scope = scopeFactory.CreateScope()) {
                var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
                success = await db.BackupEvents.AsNoTracking()
                    .Where(e => e.Id == eventId)
                    .Select(e => e.Status)
                    .FirstOrDefaultAsync(CancellationToken.None) == BackupStatuses.Success;
            }
            await chain.OnBackupFinishedAsync(eventId, success, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogError(ex, "Could not run the work chained to backup event {EventId}", eventId);
        }
    }

    /// <param name="stackId">The stack the run belongs to, or null for an instance self-backup.</param>
    /// <param name="triggeredBy">What to record on the event — see <see cref="BackupTriggers"/>.</param>
    private int CreateEvent(int? stackId, string triggeredBy) {
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
