using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Tests;

/// <summary>
/// Answers "where is Watchtower's own database" without a Docker daemon, so a test of what a restore
/// <em>decides</em> is not also a test of container detection (which <see cref="SelfPostgresLocatorTests"/>
/// covers on its own).
/// </summary>
internal sealed class FakeSelfPostgresLocator(
    DockerEngineClient docker, SelfProjectNameProvider selfProjects, IConfiguration configuration,
    IOptionsMonitor<WatchtowerOptions> options, ILogger<SelfPostgresLocator> logger)
    : SelfPostgresLocator(docker, selfProjects, configuration, options, logger) {
    /// <summary>Replaces the real locator on a test host.</summary>
    public static readonly Action<IServiceCollection> Register = services =>
        services.Replace(ServiceDescriptor.Singleton<SelfPostgresLocator, FakeSelfPostgresLocator>());

    public override Task<SelfPostgresTarget> LocateAsync(Action<string> log, CancellationToken ct) =>
        Task.FromResult(new SelfPostgresTarget(
            "test-postgres", "watchtower-postgres-1", "postgres:18-alpine", "postgres", "watchtower",
            "watchtower"));
}

/// <summary>
/// Produces a stand-in instance archive on the configured storage instead of dumping a real database,
/// so a test of what a bundle <em>contains</em> needs neither a Docker daemon nor a second PostgreSQL.
/// The bytes are a marker string rather than a real archive: nothing under test opens it — the bundle
/// carries archives, it does not read them.
/// </summary>
internal sealed class FakeInstanceBackup(
    BackupArchiveService archiveService, PostgresDumpService postgres, SelfPostgresLocator locator,
    BackupStorageFactory storageFactory, BackupRetentionRunner retention, IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options, AuditLog audit, ILogger<InstanceBackupService> logger)
    : InstanceBackupService(
        archiveService, postgres, locator, storageFactory, retention, scopeFactory, options, audit, logger) {
    /// <summary>The content every fake instance archive is written with.</summary>
    public const string Content = "fake-instance-archive";

    /// <summary>Held explicitly rather than captured, so the base class owns the only captured copy.</summary>
    private readonly BackupStorageFactory _storageFactory = storageFactory;

    /// <summary>Replaces the real service on a test host.</summary>
    public static readonly Action<IServiceCollection> Register = services =>
        services.Replace(ServiceDescriptor.Singleton<InstanceBackupService, FakeInstanceBackup>());

    public override async Task<InstanceArchiveResult> RunAsync(
        BackupOptions backup, Action<string> log, CancellationToken ct) {
        var takenAt = DateTimeOffset.UtcNow;
        var directory = BackupNaming.InstanceDirectory(backup.ResolveInstanceName());
        var fileName = BackupNaming.FileName(BackupNaming.InstanceFileStem, takenAt, encrypted: true);
        var relativePath = $"{directory}/{fileName}";

        using var storage = _storageFactory.Create(backup);
        await storage.UploadAsync(relativePath, async (stream, token) =>
            await stream.WriteAsync(Encoding.UTF8.GetBytes(Content), token), ct);
        log($"Fake instance archive written to {relativePath}");
        return new InstanceArchiveResult(
            relativePath, fileName, directory, Content.Length, takenAt, ["watchtower"]);
    }
}

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
    BackupService backupService, InstanceBackupService instanceBackupService,
    BackupBundleService bundleService, BackupChainCoordinator chain, IServiceScopeFactory scopeFactory,
    ILogger<BackupQueueService> logger)
    : BackupQueueService(backupService, instanceBackupService, bundleService, chain, scopeFactory, logger) {
    private readonly List<(int StackId, string TriggeredBy, BackupChainStep? Chain)> _enqueued = [];
    private readonly List<string> _instanceEnqueued = [];
    private readonly List<string> _bundleEnqueued = [];

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

    /// <summary>Every instance self-backup enqueue (ADR-0027), by trigger, in call order.</summary>
    public IReadOnlyList<string> InstanceEnqueued {
        get { lock (_enqueued) return [.. _instanceEnqueued]; }
    }

    public override BackupEnqueueResult Enqueue(
        int stackId, string triggeredBy, BackupChainStep? chainStep = null) {
        lock (_enqueued) {
            _enqueued.Add((stackId, triggeredBy, chainStep));
            return new BackupEnqueueResult(_enqueued.Count, "queued");
        }
    }

    /// <summary>Every bundle export enqueue (ADR-0027 §4), by trigger, in call order.</summary>
    public IReadOnlyList<string> BundleEnqueued {
        get { lock (_enqueued) return [.. _bundleEnqueued]; }
    }

    public override BackupEnqueueResult EnqueueBundleExport(string triggeredBy) {
        lock (_enqueued) {
            _bundleEnqueued.Add(triggeredBy);
            return new BackupEnqueueResult(_enqueued.Count + _bundleEnqueued.Count, "queued");
        }
    }

    public override BackupEnqueueResult EnqueueInstance(string triggeredBy) {
        // Recorded without coalescing, for the reason the stack enqueues are: nothing drains this queue,
        // so a coalesced second window would be invisible to a schedule test.
        lock (_enqueued) {
            _instanceEnqueued.Add(triggeredBy);
            return new BackupEnqueueResult(_enqueued.Count + _instanceEnqueued.Count, "queued");
        }
    }
}
