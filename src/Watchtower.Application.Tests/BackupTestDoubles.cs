using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Services;

namespace Watchtower.Application.Tests;

/// <summary>
/// Records enqueues instead of queueing them; the worker loop never starts in these tests.
/// </summary>
/// <remarks>
/// The production queue coalesces a stack that is still waiting, which is right for production and
/// would hide the second window in a schedule test — nothing ever drains it here. Recording also keeps
/// the chain step visible: what a pre-deploy or final-backup caller <em>attached</em> is the whole
/// contract of those paths, and it is invisible from a real queue that has not run yet.
/// </remarks>
internal sealed class RecordingBackupQueue(
    BackupService backupService, BackupChainCoordinator chain, IServiceScopeFactory scopeFactory,
    ILogger<BackupQueueService> logger)
    : BackupQueueService(backupService, chain, scopeFactory, logger) {
    private readonly List<(int StackId, string TriggeredBy, BackupChainStep? Chain)> _enqueued = [];

    /// <summary>Replaces the real queue on a test host.</summary>
    public static readonly Action<IServiceCollection> Register = services =>
        services.Replace(ServiceDescriptor.Singleton<BackupQueueService, RecordingBackupQueue>());

    /// <summary>Every enqueue, in call order.</summary>
    public IReadOnlyList<(int StackId, string TriggeredBy)> Enqueued {
        get { lock (_enqueued) return [.. _enqueued.Select(e => (e.StackId, e.TriggeredBy))]; }
    }

    /// <summary>Every enqueue with the work chained to it, in call order.</summary>
    public IReadOnlyList<(int StackId, string TriggeredBy, BackupChainStep? Chain)> Chained {
        get { lock (_enqueued) return [.. _enqueued]; }
    }

    public override BackupEnqueueResult Enqueue(
        int stackId, string triggeredBy, BackupChainStep? chainStep = null) {
        lock (_enqueued) {
            _enqueued.Add((stackId, triggeredBy, chainStep));
            return new BackupEnqueueResult(_enqueued.Count, "queued");
        }
    }
}
