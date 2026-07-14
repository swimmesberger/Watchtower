using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>Outcome of enqueuing a deploy: the tracking event id and its current status.</summary>
public sealed record DeployEnqueueResult(int DeployEventId, string Status);

/// <summary>
/// Manages per-stack deploy queues with smart coalescing.
/// At most one deploy runs per stack at a time, with one pending slot.
/// If a third request arrives for the same stack the caller receives the existing
/// pending event id back (coalesced), avoiding redundant work.
///
/// Registered as a singleton (so RPC handlers and the webhook endpoint can enqueue work) and
/// as an <see cref="IHostedService"/> for graceful shutdown. All database access is performed
/// through short-lived scopes resolved from <see cref="IServiceScopeFactory"/> because the
/// singleton must not capture a scoped <see cref="WatchtowerDbContext"/>.
/// </summary>
public sealed class DeployQueueService : IHostedService, IDisposable {
    private readonly ConcurrentDictionary<int, StackSlot> _slots = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GitCloneService _git;
    private readonly ComposeCliService _compose;
    private readonly DockerEngineClient _docker;
    private readonly DeployOutputBroadcaster _broadcaster;
    private readonly CaddyManager _caddy;
    private readonly ILogger<DeployQueueService> _logger;

    public DeployQueueService(
        IServiceScopeFactory scopeFactory,
        GitCloneService git,
        ComposeCliService compose,
        DockerEngineClient docker,
        DeployOutputBroadcaster broadcaster,
        CaddyManager caddy,
        ILogger<DeployQueueService> logger) {
        _scopeFactory = scopeFactory;
        _git = git;
        _compose = compose;
        _docker = docker;
        _broadcaster = broadcaster;
        _caddy = caddy;
        _logger = logger;
    }

    // IHostedService — no background loop needed; workers start on demand.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Signals all running deploys to stop and waits for them to finish.</summary>
    public async Task StopAsync(CancellationToken cancellationToken) {
        _cts.Cancel();

        var running = _slots.Values
            .Select(s => s.WorkerTask)
            .Where(t => t is not null && !t.IsCompleted)
            .Cast<Task>()
            .ToArray();

        if (running.Length > 0)
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(Timeout.Infinite, cancellationToken));
    }

    public void Dispose() => _cts.Dispose();

    /// <summary>
    /// Enqueues a deploy for <paramref name="stackId"/>. Returns the deploy event that will track it.
    /// </summary>
    /// <remarks>
    /// Three outcomes: <c>running</c> (worker started immediately), <c>queued</c> (a deploy is running,
    /// this request was stored as next-to-run), or coalesced (a deploy is running AND one is already
    /// pending — the caller receives the existing pending event id, no new row created).
    /// </remarks>
    /// <param name="removeVolumes">
    /// Optional list of named volumes to delete (via <c>compose down</c> → <c>docker volume rm</c>)
    /// after clone and BEFORE pull/up — the <c>volumes.recreate</c> data-wipe flow. Null/empty for a
    /// plain deploy. This payload is threaded through the running AND pending slots: if a recreate is
    /// coalesced onto a pending plain deploy (or vice-versa), the pending slot keeps the UNION of the
    /// volume lists so a recreate is never silently downgraded to a plain deploy.
    /// </param>
    public DeployEnqueueResult Enqueue(int stackId, string triggeredBy, IReadOnlyList<string>? removeVolumes = null) {
        var slot = _slots.GetOrAdd(stackId, _ => new StackSlot());

        lock (slot.Lock) {
            if (!slot.IsRunning) {
                var eventId = CreateEvent(stackId, triggeredBy);
                MarkRunning(eventId);
                UpdateDeployStatus(stackId, DeployStatus.Running);
                slot.IsRunning = true;
                var volumes = removeVolumes;
                slot.WorkerTask = Task.Run(() => RunSlotAsync(stackId, slot, eventId, triggeredBy, volumes, _cts.Token));
                return new DeployEnqueueResult(eventId, "running");
            }

            if (slot.PendingEventId is null) {
                var eventId = CreateEvent(stackId, triggeredBy);
                slot.PendingEventId = eventId;
                slot.PendingTriggeredBy = triggeredBy;
                slot.PendingRemoveVolumes = Normalize(removeVolumes);
                UpdateDeployStatus(stackId, DeployStatus.Queued);
                return new DeployEnqueueResult(eventId, "queued");
            }

            // Deploy running AND one already pending — coalesce onto the existing pending event.
            // Merge rule: the pending slot keeps the UNION of volume lists, and a recreate trigger
            // supersedes a plain-deploy trigger (a plain deploy must never drop a pending recreate).
            var merged = UnionVolumes(slot.PendingRemoveVolumes, removeVolumes);
            slot.PendingRemoveVolumes = merged;
            if (merged is { Count: > 0 })
                slot.PendingTriggeredBy = "volume-recreate";
            return new DeployEnqueueResult(slot.PendingEventId.Value, "queued");
        }
    }

    /// <summary>Returns null for a null/empty list, otherwise a defensive copy.</summary>
    private static IReadOnlyList<string>? Normalize(IReadOnlyList<string>? volumes) =>
        volumes is { Count: > 0 } ? volumes.Distinct(StringComparer.Ordinal).ToList() : null;

    /// <summary>Union of two volume lists (order-preserving, de-duplicated); null when both are empty.</summary>
    private static IReadOnlyList<string>? UnionVolumes(IReadOnlyList<string>? a, IReadOnlyList<string>? b) {
        if ((a is null || a.Count == 0) && (b is null || b.Count == 0)) return null;
        var set = new List<string>();
        void Add(IReadOnlyList<string>? src) {
            if (src is null) return;
            foreach (var v in src)
                if (!set.Contains(v, StringComparer.Ordinal)) set.Add(v);
        }
        Add(a);
        Add(b);
        return set;
    }

    /// <summary>Worker loop for a single stack: runs the deploy, then picks up any pending request.</summary>
    private async Task RunSlotAsync(
        int stackId, StackSlot slot, int eventId, string triggeredBy,
        IReadOnlyList<string>? removeVolumes, CancellationToken ct) {
        do {
            await ExecuteDeployAsync(stackId, eventId, removeVolumes, ct);

            lock (slot.Lock) {
                if (slot.PendingEventId is null) {
                    slot.IsRunning = false;
                    slot.WorkerTask = null;
                    return;
                }

                eventId = slot.PendingEventId.Value;
                triggeredBy = slot.PendingTriggeredBy!;
                removeVolumes = slot.PendingRemoveVolumes;
                slot.PendingEventId = null;
                slot.PendingTriggeredBy = null;
                slot.PendingRemoveVolumes = null;
            }
        } while (true);
    }

    /// <summary>
    /// Runs the full clone → [down + volume-rm] → pull → up pipeline for one deploy event.
    /// When <paramref name="removeVolumes"/> is non-empty, the stack is brought down and each named
    /// volume is deleted after the clone and before pull/up (the data-wipe recreate flow).
    /// </summary>
    private async Task ExecuteDeployAsync(
        int stackId, int eventId, IReadOnlyList<string>? removeVolumes, CancellationToken ct) {
        var stack = GetStack(stackId);
        if (stack is null) {
            CompleteEvent(eventId, "failed", "[Watchtower] Stack not found — it may have been deleted.");
            return;
        }

        MarkRunning(eventId);
        UpdateDeployStatus(stackId, DeployStatus.Running);

        var output = new StringBuilder();
        var session = _broadcaster.Create(eventId);

        void WriteHeader(string line) {
            output.AppendLine(line);
            session.Write(line);
        }
        void OnSubprocessLine(string line) => session.Write(line);

        var tempRepoDir = Path.Combine(Path.GetTempPath(), $"watchtower-clone-{Guid.NewGuid():N}");
        string? dockerConfigDir = null;
        string? envFilePath = null;

        try {
            // 1. Resolve git credential for cloning.
            var gitToken = stack.CredentialId is { } credId ? GetCredentialToken(credId) : null;

            // 2. Clone the repository.
            WriteHeader($"[Watchtower] Cloning {stack.RepositoryUrl} @ {stack.Branch}");
            var cloneResult = await _git.CloneAsync(
                stack.RepositoryUrl, stack.Branch, gitToken, tempRepoDir, OnSubprocessLine, ct);
            output.Append(cloneResult.Output);
            UpdateOutput(eventId, output.ToString());
            if (cloneResult.ExitCode != 0) {
                CompleteEvent(eventId, "failed", output.ToString());
                UpdateDeployStatus(stackId, DeployStatus.Failed);
                return;
            }

            // TrimStart ensures an accidentally absolute path is treated as relative to the cloned repo root.
            var composePath = Path.Combine(tempRepoDir, stack.ComposeFilePath.TrimStart('/', '\\'));

            // 2b. Volume-recreate flow: bring the stack down (keeps named volumes) then delete each
            // selected volume before the pull/up recreates them empty. A 409 (still referenced) fails
            // the deploy, leaving the stack down-but-not-recreated so the operator can re-run.
            if (removeVolumes is { Count: > 0 }) {
                WriteHeader($"[Watchtower] Stopping stack '{stack.ComposeProjectName}' to recreate {removeVolumes.Count} volume(s)");
                UpdateOutput(eventId, output.ToString());
                var downResult = await _compose.DownAsync(composePath, stack.ComposeProjectName, dockerConfigDir: null, ct);
                output.Append(downResult.Output);
                UpdateOutput(eventId, output.ToString());
                if (downResult.ExitCode != 0) {
                    WriteHeader("[Watchtower] compose down failed — aborting volume recreate.");
                    CompleteEvent(eventId, "failed", output.ToString());
                    UpdateDeployStatus(stackId, DeployStatus.Failed);
                    return;
                }

                foreach (var volumeName in removeVolumes) {
                    WriteHeader($"[Watchtower] Removing volume {volumeName}");
                    UpdateOutput(eventId, output.ToString());
                    try {
                        await _docker.RemoveVolumeAsync(volumeName, ct);
                    } catch (HttpRequestException ex) {
                        WriteHeader($"[Watchtower] Failed to remove volume {volumeName}: {ex.Message}");
                        CompleteEvent(eventId, "failed", output.ToString());
                        UpdateDeployStatus(stackId, DeployStatus.Failed);
                        return;
                    }
                }
            }

            // 3. Build a scoped DOCKER_CONFIG with all configured registry credentials.
            dockerConfigDir = CreateRegistryConfigDir();

            // 4. Write stack-level environment variable overrides to a temp .env file.
            var envVars = GetEnvVars(stackId);
            if (envVars.Count > 0) {
                envFilePath = Path.Combine(Path.GetTempPath(), $"watchtower-env-{Guid.NewGuid():N}.env");
                var envContent = new StringBuilder();
                foreach (var v in envVars) {
                    var safeValue = v.Value.Contains('\n') || v.Value.Contains('"')
                        ? $"\"{v.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
                        : v.Value;
                    envContent.AppendLine($"{v.Key}={safeValue}");
                }
                await File.WriteAllTextAsync(envFilePath, envContent.ToString(), ct);
                WriteHeader($"[Watchtower] Injecting {envVars.Count} environment variable(s)");
            }

            // 5. Pull updated images.
            WriteHeader($"[Watchtower] Pulling images for project '{stack.ComposeProjectName}'");
            UpdateOutput(eventId, output.ToString());
            var pullResult = await _compose.PullAsync(
                composePath, stack.ComposeProjectName, dockerConfigDir, envFilePath, OnSubprocessLine, ct);
            output.Append(pullResult.Output);
            UpdateOutput(eventId, output.ToString());
            if (pullResult.ExitCode != 0) {
                CompleteEvent(eventId, "failed", output.ToString());
                UpdateDeployStatus(stackId, DeployStatus.Failed);
                return;
            }

            // 6. Bring services up with orphan removal.
            WriteHeader("[Watchtower] Starting services");
            UpdateOutput(eventId, output.ToString());
            var upResult = await _compose.UpAsync(
                composePath, stack.ComposeProjectName, dockerConfigDir, envFilePath, OnSubprocessLine, ct);
            output.Append(upResult.Output);

            var finalStatus = upResult.ExitCode == 0 ? "success" : "failed";
            CompleteEvent(eventId, finalStatus, output.ToString());
            UpdateDeployStatus(stackId, upResult.ExitCode == 0 ? DeployStatus.Success : DeployStatus.Failed);
            if (upResult.ExitCode == 0) {
                // A successful deploy may have pulled new images; clear the cached check.
                DeleteUpdateCheck(stackId);
                // (Re)wire this stack's routes into the reverse proxy: recreated service containers must
                // rejoin the edge network and Caddy must reload. No-op when the proxy is disabled;
                // best-effort so a proxy hiccup never fails an otherwise successful deploy.
                try {
                    await _caddy.ConnectStackAsync(stackId, ct);
                    await _caddy.ApplyAsync(ct);
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Reverse-proxy reconcile after deploy of stack {StackId} failed", stackId);
                }
            }
        } catch (OperationCanceledException) {
            output.AppendLine("[Watchtower] Deploy cancelled (server shutting down).");
            CompleteEvent(eventId, "failed", output.ToString());
            UpdateDeployStatus(stackId, DeployStatus.Failed);
        } catch (Exception ex) {
            _logger.LogError(ex, "Unhandled exception during deploy of stack {StackId}", stackId);
            output.AppendLine($"[Watchtower] Exception: {ex.Message}");
            CompleteEvent(eventId, "failed", output.ToString());
            UpdateDeployStatus(stackId, DeployStatus.Failed);
        } finally {
            _broadcaster.Complete(eventId);
            SafeDelete(tempRepoDir);
            if (dockerConfigDir is not null) SafeDelete(dockerConfigDir);
            if (envFilePath is not null) SafeDeleteFile(envFilePath);
        }
    }

    // ── Scoped data access ────────────────────────────────────────────────────
    // Each helper opens a short-lived scope; the singleton must never capture a scoped DbContext.

    private int CreateEvent(int stackId, string triggeredBy) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ev = new DeployEvent {
            StackId = stackId, TriggeredBy = triggeredBy, Status = "queued", StartedAt = DateTimeOffset.UtcNow,
        };
        db.DeployEvents.Add(ev);
        db.SaveChanges();
        return ev.Id;
    }

    private void MarkRunning(int eventId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.DeployEvents.Where(e => e.Id == eventId)
            .ExecuteUpdate(s => s.SetProperty(e => e.Status, "running"));
    }

    private void UpdateOutput(int eventId, string outputText) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.DeployEvents.Where(e => e.Id == eventId)
            .ExecuteUpdate(s => s.SetProperty(e => e.Output, outputText));
    }

    private void CompleteEvent(int eventId, string status, string outputText) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.DeployEvents.Where(e => e.Id == eventId)
            .ExecuteUpdate(s => s
                .SetProperty(e => e.Status, status)
                .SetProperty(e => e.Output, outputText)
                .SetProperty(e => e.FinishedAt, now));
    }

    private void UpdateDeployStatus(int stackId, DeployStatus status) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        // last_deployed_at only advances for terminal states so it reflects completion, not start/queue.
        if (status is DeployStatus.Success or DeployStatus.Failed) {
            var now = DateTimeOffset.UtcNow;
            db.Stacks.Where(s => s.Id == stackId)
                .ExecuteUpdate(s => s
                    .SetProperty(x => x.LastDeployStatus, status)
                    .SetProperty(x => x.LastDeployedAt, now));
        } else {
            db.Stacks.Where(s => s.Id == stackId)
                .ExecuteUpdate(s => s.SetProperty(x => x.LastDeployStatus, status));
        }
    }

    private Stack? GetStack(int stackId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.Stacks.AsNoTracking().FirstOrDefault(s => s.Id == stackId);
    }

    private string? GetCredentialToken(int credentialId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.Credentials.AsNoTracking()
            .Where(c => c.Id == credentialId).Select(c => c.Token).FirstOrDefault();
    }

    private List<(string Key, string Value)> GetEnvVars(int stackId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.StackEnvVars.AsNoTracking()
            .Where(v => v.StackId == stackId)
            .OrderBy(v => v.Key)
            .Select(v => new ValueTuple<string, string>(v.Key, v.Value))
            .ToList();
    }

    private void DeleteUpdateCheck(int stackId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.StackUpdateChecks.Where(c => c.StackId == stackId).ExecuteDelete();
    }

    private string CreateRegistryConfigDir() {
        using var scope = _scopeFactory.CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<RegistryAuthBuilder>();
        return builder.CreateTempConfigDir();
    }

    private static void SafeDelete(string path) {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void SafeDeleteFile(string path) {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Per-stack mutable state. All mutations must hold <see cref="Lock"/>.</summary>
    private sealed class StackSlot {
        public readonly object Lock = new();
        public bool IsRunning;
        public int? PendingEventId;
        public string? PendingTriggeredBy;
        /// <summary>Volumes to delete for a pending recreate; null for a plain pending deploy.</summary>
        public IReadOnlyList<string>? PendingRemoveVolumes;
        public Task? WorkerTask;
    }
}
