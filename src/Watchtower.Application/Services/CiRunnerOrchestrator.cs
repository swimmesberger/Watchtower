using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Reconciles ephemeral GitHub Actions runner containers for every enabled <see cref="CiRepo"/>
/// (docs/ci-runners/design.md). Desired state per repo: <c>MaxConcurrentRunners</c> live runner
/// containers, each registered via a single-use JIT config. Runners long-poll GitHub themselves,
/// take exactly one job, then exit — the loop removes the corpse and mints a replacement.
/// No webhook and no commit polling is involved; GitHub's job queue is the trigger.
/// Runner containers are tracked purely via Docker labels so state survives Watchtower restarts.
/// </summary>
public sealed class CiRunnerOrchestrator(
    IServiceScopeFactory scopeFactory,
    DockerEngineClient docker,
    GitHubApiClient gitHub,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<CiRunnerOrchestrator> logger) : BackgroundService {

    internal const string ManagedLabel = "watchtower.managed";
    internal const string ManagedLabelValue = "ci-runner";
    internal const string RepoIdLabel = "watchtower.ci.repo-id";
    internal const string RepoLabel = "watchtower.ci.repo";
    internal const string RunnerIdLabel = "watchtower.ci.runner-id";

    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<int, CiRepoRunnerStatus> _status = new();

    /// <summary>Snapshot of per-repo runner state for <c>ci.getRunnerStatus</c>/<c>ci.listRepos</c>.</summary>
    public IReadOnlyDictionary<int, CiRepoRunnerStatus> Status => _status;

    /// <summary>Wakes the reconcile loop immediately (called by ci.* handlers after config changes).</summary>
    public void RequestReconcile() {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        // Small startup delay so reconcile doesn't race host/db initialization.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested) {
            try {
                await ReconcileAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                logger.LogError(ex, "CI runner reconcile pass failed");
            }

            var seconds = Math.Clamp(options.CurrentValue.Ci.ReconcileIntervalSeconds, 5, 300);
            try {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds), ct), _wake.WaitAsync(ct));
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct) {
        List<CiRepo> repos;
        await using (var scope = scopeFactory.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            repos = await db.CiRepos.AsNoTracking().Include(r => r.Credential).ToListAsync(ct);
        }

        var containers = await docker.ListContainersByLabelsAsync([$"{ManagedLabel}={ManagedLabelValue}"], ct);
        var byRepoId = containers
            .Where(c => c.Labels.ContainsKey(RepoIdLabel))
            .ToLookup(c => c.Labels[RepoIdLabel]);

        var knownIds = new HashSet<string>(repos.Select(r => r.Id.ToString()));

        // Orphans: containers whose repo was deleted from the DB.
        foreach (var group in byRepoId.Where(g => !knownIds.Contains(g.Key)))
            foreach (var container in group)
                await RemoveRunnerContainerAsync(container.Id, "repo removed", ct);
        foreach (var staleId in _status.Keys.Where(id => !knownIds.Contains(id.ToString())).ToList())
            _status.TryRemove(staleId, out _);

        foreach (var repo in repos) {
            ct.ThrowIfCancellationRequested();
            try {
                await ReconcileRepoAsync(repo, byRepoId[repo.Id.ToString()].ToList(), ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                logger.LogError(ex, "Reconcile failed for CI repo {Repo}", repo.FullName);
                RecordFailure(repo.Id, ex.Message);
            }
        }
    }

    private async Task ReconcileRepoAsync(CiRepo repo, IReadOnlyList<DockerContainerInfo> containers, CancellationToken ct) {
        var status = _status.GetOrAdd(repo.Id, _ => new CiRepoRunnerStatus());

        // Exited ephemeral runners are normal after a completed job — reap them. A non-zero exit
        // is only suspicious when the runner died immediately (bad image/config), which the
        // failure backoff below catches via spawn errors; job-level failures live in GitHub.
        foreach (var dead in containers.Where(c => !IsRunning(c))) {
            await TryDeleteRegistrationAsync(repo, dead, ct);
            await RemoveRunnerContainerAsync(dead.Id, "runner finished", ct);
        }
        var running = containers.Where(IsRunning).ToList();

        if (!repo.Enabled) {
            foreach (var container in running) {
                await TryDeleteRegistrationAsync(repo, container, ct);
                await RemoveRunnerContainerAsync(container.Id, "repo disabled", ct);
            }
            status.Update(desired: 0, running: 0);
            return;
        }

        var desired = Math.Clamp(repo.MaxConcurrentRunners, 1, 16);
        if (running.Count >= desired || status.InBackoff(DateTimeOffset.UtcNow)) {
            status.Update(desired, running.Count);
            return;
        }

        if (repo.Credential is null) {
            RecordFailure(repo.Id, "No credential configured.");
            return;
        }

        var ci = options.CurrentValue.Ci;
        var image = string.IsNullOrWhiteSpace(repo.RunnerImage) ? ci.RunnerImage : repo.RunnerImage;
        await EnsureImageAsync(image, ct);

        for (var i = running.Count; i < desired; i++) {
            await SpawnRunnerAsync(repo, image, ci, ct);
            status.RecordSpawn();
        }
        status.Update(desired, desired);
        status.ClearFailure();
    }

    private async Task SpawnRunnerAsync(CiRepo repo, string image, CiOptions ci, CancellationToken ct) {
        var instance = ci.ResolveInstanceName();
        var runnerName = $"watchtower-{instance}-{Slug(repo.Name)}-{Guid.NewGuid().ToString("N")[..8]}";
        var labels = new List<string> { "self-hosted", "watchtower", instance };
        if (!string.IsNullOrWhiteSpace(repo.ExtraLabels))
            labels.AddRange(repo.ExtraLabels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var jit = await gitHub.GenerateJitConfigAsync(repo.Owner, repo.Name, runnerName, labels, repo.Credential!.Token, ct);

        var binds = new List<string> {
            // Warm toolcache shared by all runners of this repo; the workspace itself stays ephemeral.
            $"watchtower-ci-tool-{Slug(repo.FullName)}:/home/runner/_work/_tool",
        };
        if (repo.AllowDockerSocket)
            binds.Add("/var/run/docker.sock:/var/run/docker.sock");

        var body = new DockerCreateContainerBody {
            Image = image,
            // The JIT config is single-use — its visibility in `docker inspect` is acceptable.
            Cmd = ["/home/runner/run.sh", "--jitconfig", jit.EncodedJitConfig],
            Labels = new Dictionary<string, string> {
                [ManagedLabel] = ManagedLabelValue,
                [RepoIdLabel] = repo.Id.ToString(),
                [RepoLabel] = repo.FullName.ToLowerInvariant(),
                [RunnerIdLabel] = jit.RunnerId.ToString(),
            },
            HostConfig = new DockerCreateHostConfig { Binds = binds.ToArray() },
        };

        var containerId = await docker.CreateContainerAsync(body, runnerName, ct);
        await docker.StartContainerAsync(containerId, ct);
        logger.LogInformation("Spawned CI runner {Runner} for {Repo}", runnerName, repo.FullName);
    }

    /// <summary>Best-effort deregistration for runners that never took a job (JIT ids are on the container labels).</summary>
    private async Task TryDeleteRegistrationAsync(CiRepo repo, DockerContainerInfo container, CancellationToken ct) {
        if (repo.Credential is null || !container.Labels.TryGetValue(RunnerIdLabel, out var idText) || !long.TryParse(idText, out var runnerId))
            return;
        try {
            await gitHub.TryDeleteRunnerAsync(repo.Owner, repo.Name, runnerId, repo.Credential.Token, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogDebug(ex, "Could not deregister runner {RunnerId} for {Repo}", runnerId, repo.FullName);
        }
    }

    private async Task RemoveRunnerContainerAsync(string containerId, string reason, CancellationToken ct) {
        try {
            await docker.StopContainerAsync(containerId, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogDebug(ex, "Stop failed for CI runner container {Id} (may already be stopped)", containerId);
        }
        try {
            await docker.RemoveContainerAsync(containerId, ct);
            logger.LogInformation("Removed CI runner container {Id} ({Reason})", containerId[..Math.Min(12, containerId.Length)], reason);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "Failed to remove CI runner container {Id}", containerId);
        }
    }

    private async Task EnsureImageAsync(string image, CancellationToken ct) {
        try {
            await docker.InspectImageAsync(image, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogInformation(ex, "Runner image {Image} not present locally; pulling", image);
            await docker.PullImageAsync(image, ct: ct);
        }
    }

    private void RecordFailure(int repoId, string message) =>
        _status.GetOrAdd(repoId, _ => new CiRepoRunnerStatus()).RecordFailure(message);

    private static bool IsRunning(DockerContainerInfo c) =>
        c.State.Equals("running", StringComparison.OrdinalIgnoreCase)
        || c.State.Equals("restarting", StringComparison.OrdinalIgnoreCase)
        || c.State.Equals("created", StringComparison.OrdinalIgnoreCase);

    private static string Slug(string value) {
        var chars = value.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray();
        return new string(chars).Trim('-');
    }
}

/// <summary>Mutable per-repo runner state surfaced through <c>ci.getRunnerStatus</c>.</summary>
public sealed class CiRepoRunnerStatus {
    private int _consecutiveFailures;

    public int DesiredRunners { get; private set; }
    public int RunningRunners { get; private set; }
    public long TotalSpawned { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastErrorAt { get; private set; }
    public DateTimeOffset? BackoffUntil { get; private set; }

    internal void Update(int desired, int running) {
        DesiredRunners = desired;
        RunningRunners = running;
    }

    internal void RecordSpawn() => TotalSpawned++;

    internal void RecordFailure(string message) {
        LastError = message;
        LastErrorAt = DateTimeOffset.UtcNow;
        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 8);
        // 30s, 60s, 2m, 4m … capped at ~2h so a bad PAT doesn't hammer the GitHub API.
        BackoffUntil = DateTimeOffset.UtcNow.AddSeconds(30 * Math.Pow(2, _consecutiveFailures - 1));
    }

    internal void ClearFailure() {
        _consecutiveFailures = 0;
        LastError = null;
        LastErrorAt = null;
        BackoffUntil = null;
    }

    internal bool InBackoff(DateTimeOffset now) => BackoffUntil is { } until && until > now;
}
