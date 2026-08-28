using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    CiActionsConfigSync actionsConfig,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<CiRunnerOrchestrator> logger) : BackgroundService {

    internal const string ManagedLabel = "watchtower.managed";
    internal const string ManagedLabelValue = "ci-runner";
    internal const string WarmerLabelValue = "ci-warmer";
    internal const string VolumeInitLabelValue = "ci-volume-init";
    internal const string RepoIdLabel = "watchtower.ci.repo-id";
    internal const string RepoLabel = "watchtower.ci.repo";
    internal const string RunnerIdLabel = "watchtower.ci.runner-id";
    internal const string ProfileHashLabel = "watchtower.ci.profile-hash";
    internal const string SpecHashLabel = "watchtower.ci.spec-hash";

    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<int, CiRepoRunnerStatus> _status = new();
    private string? _warnedInvalidSnapshotter;
    private string? _lastResolvedSnapshotter;

    /// <summary>Snapshot of per-repo runner state for <c>ci.getRunnerStatus</c>/<c>ci.listRepos</c>.</summary>
    public IReadOnlyDictionary<int, CiRepoRunnerStatus> Status => _status;

    /// <summary>
    /// Drops the Actions-sync failure defer for one repo so the next pass retries immediately.
    /// Called by <c>ci.updateRepo</c>, <c>ci.setReleaseSecretsSync</c> and
    /// <c>products.rotateReleaseToken</c> on every save: a config change is the operator saying "try
    /// again now" — typically right after fixing the PAT's permissions.
    /// </summary>
    public void ClearActionsSyncBackoff(int repoId) {
        if (_status.TryGetValue(repoId, out var status))
            status.ClearActionsSyncRetry();
    }

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
        // Volume-init containers are awaited within a pass, so any one found here is a leftover
        // from a crashed pass — just remove it (the next spawn re-runs the init).
        foreach (var stale in await docker.ListContainersByLabelsAsync([$"{ManagedLabel}={VolumeInitLabelValue}"], ct))
            await RemoveRunnerContainerAsync(stale.Id, "stale volume init", ct);

        var warmers = await docker.ListContainersByLabelsAsync([$"{ManagedLabel}={WarmerLabelValue}"], ct);
        var warmersByRepoId = warmers
            .Where(c => c.Labels.ContainsKey(RepoIdLabel))
            .ToLookup(c => c.Labels[RepoIdLabel]);

        var knownIds = new HashSet<string>(repos.Select(r => r.Id.ToString()));
        // Resolved once per pass and only when a repo exists to receive it — most installs have
        // no CI repos and should not pay a GET /info every interval.
        var buildkitConfig = repos.Count > 0 ? await ResolveBuildkitConfigAsync(ct) : string.Empty;

        // Orphans: containers whose repo was deleted from the DB.
        foreach (var group in byRepoId.Where(g => !knownIds.Contains(g.Key)))
            foreach (var container in group)
                await RemoveRunnerContainerAsync(container.Id, "repo removed", ct);
        foreach (var group in warmersByRepoId.Where(g => !knownIds.Contains(g.Key)))
            foreach (var container in group)
                await RemoveRunnerContainerAsync(container.Id, "repo removed", ct);
        foreach (var staleId in _status.Keys.Where(id => !knownIds.Contains(id.ToString())).ToList())
            _status.TryRemove(staleId, out _);

        foreach (var repo in repos) {
            ct.ThrowIfCancellationRequested();
            try {
                await ReconcileWarmerAsync(repo, warmersByRepoId[repo.Id.ToString()].ToList(), buildkitConfig, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                // Warm problems must never take the runner reconcile down with them.
                logger.LogWarning(ex, "Toolcache warm reconcile failed for CI repo {Repo}", repo.FullName);
            }
            try {
                await ReconcileRepoAsync(repo, byRepoId[repo.Id.ToString()].ToList(), buildkitConfig, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                logger.LogError(ex, "Reconcile failed for CI repo {Repo}", repo.FullName);
                RecordFailure(repo.Id, ex.Message);
            }
            // After the pass, not from the listing that opened it: this one reflects what the pass
            // itself reaped, retired and spawned, so the operator's next poll shows the runner that
            // was just created rather than the one that is already gone. Outside the try above so a
            // failed reconcile still leaves the UI an accurate picture of what is actually there.
            await SnapshotRunnersAsync(repo, ct);
            try {
                // Both Actions-config contributors (registry credentials, release configuration). Each
                // is already isolated inside the service; this catch is the backstop for anything that
                // escapes it, because secret-sync problems must never take the runner reconcile down.
                await actionsConfig.SyncActionsConfigAsync(
                    repo, _status.GetOrAdd(repo.Id, _ => new CiRepoRunnerStatus()), ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                logger.LogWarning(ex, "Actions config sync failed for CI repo {Repo}", repo.FullName);
            }
        }
    }

    /// <summary>
    /// The default BuildKit configuration every runner of this pass receives, resolved once per
    /// pass: the daemon's insecure registries, plus the snapshotter — auto-detected from the host
    /// by default, overridable via <c>Ci:BuildkitSnapshotter</c>
    /// (<see cref="CiBuildkitConfig.ResolveSnapshotter"/>). Failures degrade — an unreadable
    /// <c>/info</c> or <c>/proc/filesystems</c> means fewer facts this pass, and an invalid option
    /// value falls back to detection with a warning (logged once per distinct value, not once per
    /// pass) — because a worse buildkitd config must never cost a repo its runners.
    /// </summary>
    private async Task<string> ResolveBuildkitConfigAsync(CancellationToken ct) {
        DockerEngineInfo engineInfo = new();
        try {
            engineInfo = await docker.GetEngineInfoAsync(ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogDebug(ex, "Could not read the engine info for the runner buildkitd config");
        }

        string procFilesystems;
        try {
            procFilesystems = await File.ReadAllTextAsync("/proc/filesystems", ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            procFilesystems = string.Empty; // Non-Linux dev host, or a locked-down container.
        }

        var configured = options.CurrentValue.Ci.BuildkitSnapshotter;
        string? snapshotter;
        try {
            snapshotter = CiBuildkitConfig.ResolveSnapshotter(configured, engineInfo.Driver, procFilesystems);
        } catch (ArgumentException) {
            if (_warnedInvalidSnapshotter != configured) {
                logger.LogWarning(
                    "Falling back to snapshotter auto-detection: invalid Ci:BuildkitSnapshotter value "
                    + "{Snapshotter} — expected 'auto', 'none', or a BuildKit snapshotter name such as "
                    + "'overlayfs', 'fuse-overlayfs' or 'native'", configured);
                _warnedInvalidSnapshotter = configured;
            }
            snapshotter = CiBuildkitConfig.ResolveSnapshotter(
                CiBuildkitConfig.SnapshotterAuto, engineInfo.Driver, procFilesystems);
        }

        // The decision is a host fact operators will want in the log exactly once, not per pass.
        if (_lastResolvedSnapshotter != (snapshotter ?? "(none)")) {
            _lastResolvedSnapshotter = snapshotter ?? "(none)";
            logger.LogInformation(
                "Runner buildkitd config: snapshotter {Snapshotter} (storage driver {Driver})",
                _lastResolvedSnapshotter, engineInfo.Driver ?? "(unknown)");
        }

        return CiBuildkitConfig.Build(engineInfo.InsecureRegistries(), snapshotter);
    }

    private async Task ReconcileRepoAsync(
        CiRepo repo, IReadOnlyList<DockerContainerInfo> containers, string buildkitConfig, CancellationToken ct) {
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
        if (status.InBackoff(DateTimeOffset.UtcNow)) {
            status.Update(desired, running.Count);
            return;
        }

        if (repo.Credential is null) {
            RecordFailure(repo.Id, "No credential configured.");
            return;
        }

        var ci = options.CurrentValue.Ci;
        var image = ResolveImage(repo, ci);

        // Before topping the slots up: retire runners spawned under settings that have since
        // changed, so the operator's next build runs on the settings they just saved.
        running = await RecycleStaleRunnersAsync(repo, running, ComputeSpecHash(repo, image), ct);

        // Volumes converge every pass, not only when a slot is spawned: a changed buildkitd
        // config (registry added, snapshotter set) must reach the buildx volume even while every
        // runner slot is occupied — the file is read at job time, no respawn involved. Costs a
        // stamp compare per pass; the init container only runs on a change.
        await EnsureImageAsync(image, ct);
        await EnsureVolumesReadyAsync(repo, image, status, buildkitConfig, ct);

        if (running.Count >= desired) {
            status.Update(desired, running.Count);
            return;
        }

        for (var i = running.Count; i < desired; i++) {
            await SpawnRunnerAsync(repo, image, ci, ct);
            status.RecordSpawn();
        }
        status.Update(desired, desired);
        status.ClearFailure();
    }

    /// <summary>
    /// Drops runner containers whose <see cref="SpecHashLabel"/> no longer matches the repo's
    /// current settings, returning the ones that stay. Idleness is established by deregistering the
    /// runner at GitHub first: GitHub refuses to delete a runner that is executing a job, so a busy
    /// runner is simply kept and recycled on a later pass, once its job has finished and the
    /// ephemeral container has exited on its own.
    /// <para>
    /// The first refusal ends the pass: a repo whose runners are all working a build would
    /// otherwise spend one API call per runner per reconcile interval learning the same thing.
    /// This way a stale-but-busy repo costs a single call every interval, while idle runners are
    /// still all replaced within one pass.
    /// </para>
    /// </summary>
    private async Task<List<DockerContainerInfo>> RecycleStaleRunnersAsync(
        CiRepo repo, List<DockerContainerInfo> running, string specHash, CancellationToken ct) {
        var kept = new List<DockerContainerInfo>(running.Count);
        var busy = false;
        foreach (var container in running) {
            var containerHash = container.Labels.GetValueOrDefault(SpecHashLabel);
            if (containerHash == specHash || busy) {
                kept.Add(container);
                continue;
            }
            if (!await TryDeleteRegistrationAsync(repo, container, ct)) {
                busy = true;
                kept.Add(container);
                continue;
            }
            await RemoveRunnerContainerAsync(
                container.Id, $"runner settings changed ({containerHash ?? "unlabelled"} → {specHash})", ct);
        }
        return kept;
    }

    /// <summary>
    /// Records the repo's runner containers on its status for the UI's runner table. Read from
    /// Docker rather than from Watchtower state on purpose — the containers <em>are</em> the state
    /// (docs/ci-runners/design.md), so a runner started by a previous Watchtower process, or one
    /// that died in a way the loop has not reaped yet, shows up exactly as it is.
    /// A snapshot failure is not a reconcile failure: it leaves the previous list in place.
    /// </summary>
    private async Task SnapshotRunnersAsync(CiRepo repo, CancellationToken ct) {
        try {
            var containers = await docker.ListContainersByLabelsAsync(
                [$"{ManagedLabel}={ManagedLabelValue}", $"{RepoIdLabel}={repo.Id}"], ct);
            var specHash = ComputeSpecHash(repo, ResolveImage(repo, options.CurrentValue.Ci));
            _status.GetOrAdd(repo.Id, _ => new CiRepoRunnerStatus())
                .UpdateRunners(containers.Where(IsRunning).Select(c => Describe(c, specHash)).ToList());
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogDebug(ex, "Could not snapshot runner containers for {Repo}", repo.FullName);
        }
    }

    /// <summary>Projects one runner container onto what the UI shows about it.</summary>
    internal static CiRunnerContainer Describe(DockerContainerInfo container, string specHash) => new(
        container.Id[..Math.Min(12, container.Id.Length)],
        container.Names.FirstOrDefault()?.TrimStart('/') ?? "(unnamed)",
        container.Image,
        container.State,
        container.Status,
        container.Created > 0 ? DateTimeOffset.FromUnixTimeSeconds(container.Created) : null,
        long.TryParse(container.Labels.GetValueOrDefault(RunnerIdLabel), out var runnerId) ? runnerId : null,
        container.Labels.GetValueOrDefault(SpecHashLabel) != specHash);

    private static string ResolveImage(CiRepo repo, CiOptions ci) =>
        string.IsNullOrWhiteSpace(repo.RunnerImage) ? ci.RunnerImage : repo.RunnerImage;

    private async Task SpawnRunnerAsync(CiRepo repo, string image, CiOptions ci, CancellationToken ct) {
        var instance = ci.ResolveInstanceName();
        var runnerName = $"watchtower-{instance}-{Slug(repo.Name)}-{Guid.NewGuid().ToString("N")[..8]}";
        var labels = new List<string> { "self-hosted", "watchtower", instance };
        if (!string.IsNullOrWhiteSpace(repo.ExtraLabels))
            labels.AddRange(repo.ExtraLabels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var jit = await gitHub.GenerateJitConfigAsync(repo.Owner, repo.Name, runnerName, labels, repo.Credential!.Token, ct);

        var body = BuildRunnerContainerBody(
            repo, image, jit.EncodedJitConfig, jit.RunnerId, HostSupplementaryGroups.Current());
        var containerId = await docker.CreateContainerAsync(body, runnerName, ct);
        await docker.StartContainerAsync(containerId, ct);
        logger.LogInformation("Spawned CI runner {Runner} for {Repo}", runnerName, repo.FullName);
    }

    /// <summary>
    /// The runner container spec. No mount may live under <c>/home/runner/_work</c>: dockerd
    /// creates missing mountpoint parents as root, and a root-owned <c>_work</c> stops the runner
    /// user from creating <c>_work/_temp</c> when a job arrives (the workspace stays ephemeral by
    /// leaving <c>_work</c> to the runner itself).
    /// </summary>
    /// <param name="hostGroupIds">
    /// Watchtower's own supplementary group ids (<see cref="HostSupplementaryGroups"/>), applied only
    /// to docker-socket runners. The socket is owned by the host's <c>docker</c> group, while the
    /// runner image gives its non-root <c>runner</c> user a <c>docker</c> group of its own with a
    /// fixed id of 123 — so without these ids the mounted socket is there but every <c>docker</c>
    /// call in a job dies with "permission denied while trying to connect to the Docker daemon
    /// socket". Same mechanism the self-update coordinator uses.
    /// </param>
    internal static DockerCreateContainerBody BuildRunnerContainerBody(
        CiRepo repo, string image, string encodedJitConfig, long runnerId, string[] hostGroupIds) {
        var binds = new List<string> {
            // Warm toolcache shared by all runners of this repo.
            $"{ToolVolumeName(repo)}:{CiWarmerScript.ToolCacheDir}",
            // Package caches (NuGet/npm/Go modules) survive across jobs via the env vars below.
            $"{PkgVolumeName(repo)}:{PkgCacheDir}",
            // buildx state dir carrying the Watchtower-generated buildkitd.default.toml (written by
            // the volume-init container), which `docker buildx create` picks up whenever the
            // workflow passes no config of its own. A volume rather than a file bind on purpose:
            // dockerd would create the bind's parents root-owned (the _work trap again), while the
            // volume root is chowned by the same init that writes the file.
            $"{BuildxVolumeName(repo)}:{BuildxConfigDir}",
        };
        if (repo.AllowDockerSocket)
            binds.Add("/var/run/docker.sock:/var/run/docker.sock");

        return new DockerCreateContainerBody {
            Image = image,
            // The JIT config is single-use — its visibility in `docker inspect` is acceptable.
            Cmd = ["/home/runner/run.sh", "--jitconfig", encodedJitConfig],
            // Runner-process env is inherited by job steps. RUNNER_TOOL_CACHE points setup-* actions
            // at the warmed toolcache volume; DOTNET_INSTALL_DIR points setup-dotnet (which does not
            // use RUNNER_TOOL_CACHE) at the warmed SDK dir so it skips the download; the
            // package-manager caches land on the pkg volume instead of the ephemeral workspace;
            // BUILDX_CONFIG points buildx at the volume holding the default buildkitd config.
            Env = [
                $"RUNNER_TOOL_CACHE={CiWarmerScript.ToolCacheDir}",
                $"DOTNET_INSTALL_DIR={CiWarmerScript.ToolCacheDir}/dotnet",
                $"NUGET_PACKAGES={PkgCacheDir}/nuget",
                $"npm_config_cache={PkgCacheDir}/npm",
                $"GOMODCACHE={PkgCacheDir}/gomod",
                $"BUILDX_CONFIG={BuildxConfigDir}",
            ],
            Labels = new Dictionary<string, string> {
                [ManagedLabel] = ManagedLabelValue,
                [RepoIdLabel] = repo.Id.ToString(),
                [RepoLabel] = repo.FullName.ToLowerInvariant(),
                [RunnerIdLabel] = runnerId.ToString(),
                [SpecHashLabel] = ComputeSpecHash(repo, image),
            },
            HostConfig = new DockerCreateHostConfig {
                Binds = binds.ToArray(),
                GroupAdd = repo.AllowDockerSocket && hostGroupIds.Length > 0 ? hostGroupIds : null,
            },
        };
    }

    /// <summary>
    /// Identifies the repo settings baked into a runner container at spawn time. Runners are
    /// long-lived while idle (they sit long-polling GitHub until a job arrives), so a settings
    /// change would otherwise only take effect after the current runner happened to consume one
    /// more job — an operator who ticks "allow docker socket" would watch the very next build fail
    /// on the socket they just granted. The hash goes on the container as
    /// <see cref="SpecHashLabel"/>; a mismatch makes the reconcile loop recycle the runner.
    /// </summary>
    internal static string ComputeSpecHash(CiRepo repo, string image) {
        // The leading "2|" is a container-shape version: bumped when Watchtower itself changes what
        // every runner is spawned with (the BUILDX_CONFIG volume + env, added for the default
        // buildkitd config), so idle runners from before the change are recycled once instead of
        // running without it until they happen to consume a job.
        var material = $"2|{image}|{repo.AllowDockerSocket}|{repo.ExtraLabels ?? string.Empty}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    // ── Cache volume initialization ──────────────────────────────────────────

    /// <summary>Where the volume-init container mounts the cache and buildx volumes.</summary>
    internal const string VolumeInitMountRoot = "/watchtower-volume-init";

    /// <summary>Env var the volume-init container reads the buildkitd config content from.</summary>
    internal const string BuildkitConfigEnvVar = "WATCHTOWER_BUILDKITD_CONFIG";

    /// <summary>The default-config file name buildx probes inside <c>$BUILDX_CONFIG</c>.</summary>
    internal const string BuildkitConfigFileName = "buildkitd.default.toml";

    /// <summary>
    /// Prepares the repo's volumes before anything mounts them: chowns the roots to the runner user
    /// (a fresh named volume is root-owned, and both the runner and the warmer run as the image's
    /// non-root <c>runner</c> user; contents are created by that user afterwards, so non-recursive
    /// is enough) and writes the current default buildkitd config into the buildx volume. Re-runs
    /// whenever the config content changes — the stamp on the status carries what was last written —
    /// so a registry added at runtime or an edited snapshotter reaches jobs within one pass; the
    /// whole init is idempotent, so re-running after a restart is fine.
    /// </summary>
    private async Task EnsureVolumesReadyAsync(
        CiRepo repo, string image, CiRepoRunnerStatus status, string buildkitConfig, CancellationToken ct) {
        var stamp = CiBuildkitConfig.Stamp(buildkitConfig);
        if (status.VolumesReadyStamp == stamp)
            return;

        var name = $"watchtower-ci-volinit-{Slug(repo.Name)}-{Guid.NewGuid().ToString("N")[..8]}";
        var containerId = await docker.CreateContainerAsync(
            BuildVolumeInitContainerBody(repo, image, buildkitConfig), name, ct);
        try {
            await docker.StartContainerAsync(containerId, ct);
            // The chown and the one-file write are instant; the timeout only guards the loop
            // against a wedged daemon.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var exitCode = await docker.WaitContainerAsync(containerId, timeout.Token);
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"Cache volume init for {repo.FullName} exited with code {exitCode} — "
                    + "the runner image may lack the 'runner' user.");
        } finally {
            await RemoveRunnerContainerAsync(containerId, "volume init finished", ct);
        }
        status.MarkVolumesReady(stamp);
    }

    /// <summary>
    /// The one-shot root container that chowns the volume roots to the runner user and writes the
    /// default buildkitd config into the buildx volume. The config travels as an env var and is
    /// written with <c>printf '%s'</c>, so its content never touches shell syntax; it is generated
    /// from validated inputs anyway (<see cref="CiBuildkitConfig"/>). The file itself is chowned
    /// too so a later runner could replace it — buildx also writes its builder state next to it.
    /// </summary>
    internal static DockerCreateContainerBody BuildVolumeInitContainerBody(
        CiRepo repo, string image, string buildkitConfig) => new() {
        Image = image,
        User = "root",
        Cmd = [
            "/bin/bash", "-c",
            $"set -eu\n"
            + $"printf '%s' \"${BuildkitConfigEnvVar}\" > {VolumeInitMountRoot}/buildx/{BuildkitConfigFileName}\n"
            + $"chown runner:runner {VolumeInitMountRoot}/tool {VolumeInitMountRoot}/pkg "
            + $"{VolumeInitMountRoot}/buildx {VolumeInitMountRoot}/buildx/{BuildkitConfigFileName}",
        ],
        Env = [$"{BuildkitConfigEnvVar}={buildkitConfig}"],
        Labels = new Dictionary<string, string> {
            [ManagedLabel] = VolumeInitLabelValue,
            [RepoIdLabel] = repo.Id.ToString(),
            [RepoLabel] = repo.FullName.ToLowerInvariant(),
        },
        HostConfig = new DockerCreateHostConfig {
            Binds = [
                $"{ToolVolumeName(repo)}:{VolumeInitMountRoot}/tool",
                $"{PkgVolumeName(repo)}:{VolumeInitMountRoot}/pkg",
                $"{BuildxVolumeName(repo)}:{VolumeInitMountRoot}/buildx",
            ],
            NetworkMode = "none",
        },
    };

    // ── Toolcache warming (docs/ci-runners/design.md) ────────────────────────

    /// <summary>Where the package-cache volume (NuGet/npm/Go modules) is mounted in runners.</summary>
    internal const string PkgCacheDir = "/home/runner/_pkg";

    /// <summary>Per-repo toolcache volume, shared by runners and the warmer.</summary>
    internal static string ToolVolumeName(CiRepo repo) => $"watchtower-ci-tool-{Slug(repo.FullName)}";

    /// <summary>Per-repo package-cache volume.</summary>
    internal static string PkgVolumeName(CiRepo repo) => $"watchtower-ci-pkg-{Slug(repo.FullName)}";

    /// <summary>
    /// Where the per-repo buildx volume is mounted in runners, exported as <c>BUILDX_CONFIG</c>.
    /// Holds the Watchtower-generated <see cref="BuildkitConfigFileName"/> plus whatever builder
    /// state buildx keeps for itself. Not under <c>/home/runner/_work</c> (see the class remarks)
    /// and not under <c>~/.docker</c> — a mount there would leave the runner user's own config
    /// directory root-owned and break the next <c>docker login</c> in a job.
    /// </summary>
    internal const string BuildxConfigDir = "/home/runner/_buildx";

    /// <summary>Per-repo buildx volume (default buildkitd config + buildx state).</summary>
    internal static string BuildxVolumeName(CiRepo repo) => $"watchtower-ci-buildx-{Slug(repo.FullName)}";

    /// <summary>
    /// Converges the repo's toolcache volume on its detected toolchain profile: reaps finished
    /// warmer containers (persisting success/failure on the repo), then spawns a one-shot warmer
    /// when the current profile hash differs from the last successfully warmed one. Warmers get the
    /// cache volume and nothing else — no PAT, no JIT config, no Docker socket; they only download
    /// public SDK releases. Failures are surfaced on the repo and retried with a fixed backoff;
    /// they never block runners (a cold cache just means jobs download their own tools).
    /// </summary>
    private async Task ReconcileWarmerAsync(
        CiRepo repo, IReadOnlyList<DockerContainerInfo> warmers, string buildkitConfig, CancellationToken ct) {
        var status = _status.GetOrAdd(repo.Id, _ => new CiRepoRunnerStatus());

        foreach (var dead in warmers.Where(c => !IsRunning(c))) {
            await ReapWarmerAsync(repo, dead, status, ct);
        }
        var running = warmers.Where(IsRunning).ToList();

        if (!repo.Enabled) {
            foreach (var container in running)
                await RemoveRunnerContainerAsync(container.Id, "repo disabled", ct);
            status.SetWarmerRunning(false);
            return;
        }

        status.SetWarmerRunning(running.Count > 0);
        if (running.Count > 0)
            return;

        var profile = CiToolchainProfile.FromJson(repo.ToolchainProfileJson);
        if (profile is null)
            return; // Nothing detected yet — the next deploy of a linked stack fills the profile.

        var hash = profile.ComputeHash();
        if (hash == repo.WarmedProfileHash || status.InWarmBackoff(DateTimeOffset.UtcNow))
            return;

        var script = CiWarmerScript.Build(profile);
        if (script is null) {
            // Nothing warmable in the profile (e.g. Dockerfile only) — record it as converged so
            // the loop doesn't re-evaluate the same empty profile every pass.
            await PersistWarmResultAsync(repo.Id, hash, error: null, ct);
            return;
        }

        var ci = options.CurrentValue.Ci;
        var image = string.IsNullOrWhiteSpace(repo.RunnerImage) ? ci.RunnerImage : repo.RunnerImage;
        await EnsureImageAsync(image, ct);
        // The warmer runs as the image's non-root runner user too, so it needs the chown as well.
        await EnsureVolumesReadyAsync(repo, image, status, buildkitConfig, ct);

        var name = $"watchtower-ci-warm-{Slug(repo.Name)}-{Guid.NewGuid().ToString("N")[..8]}";
        var body = new DockerCreateContainerBody {
            Image = image,
            Cmd = ["/bin/bash", "-c", script],
            Labels = new Dictionary<string, string> {
                [ManagedLabel] = WarmerLabelValue,
                [RepoIdLabel] = repo.Id.ToString(),
                [RepoLabel] = repo.FullName.ToLowerInvariant(),
                [ProfileHashLabel] = hash,
            },
            HostConfig = new DockerCreateHostConfig {
                Binds = [$"{ToolVolumeName(repo)}:{CiWarmerScript.ToolCacheDir}"],
            },
        };
        var containerId = await docker.CreateContainerAsync(body, name, ct);
        await docker.StartContainerAsync(containerId, ct);
        status.SetWarmerRunning(true);
        logger.LogInformation(
            "Spawned toolcache warmer {Warmer} for {Repo} ({Toolchains})",
            name, repo.FullName, string.Join(", ", profile.Toolchains.Select(t => $"{t.Kind} {t.Version}")));
    }

    /// <summary>Reads a finished warmer's outcome, persists it on the repo, and removes the container.</summary>
    private async Task ReapWarmerAsync(CiRepo repo, DockerContainerInfo container, CiRepoRunnerStatus status, CancellationToken ct) {
        var hash = container.Labels.GetValueOrDefault(ProfileHashLabel);
        try {
            var details = await docker.InspectContainerAsync(container.Id, ct);
            if (details.State?.ExitCode == 0 && hash is not null) {
                await PersistWarmResultAsync(repo.Id, hash, error: null, ct);
                logger.LogInformation("Toolcache warm for {Repo} succeeded (profile {Hash})", repo.FullName, hash);
            } else {
                var tail = new List<string>();
                try {
                    await foreach (var line in docker.StreamLogsAsync(container.Id, tail: 30, follow: false, ct))
                        tail.Add(line);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    logger.LogDebug(ex, "Could not read warmer logs for {Repo}", repo.FullName);
                }
                var message = $"Warmer exited with code {details.State?.ExitCode ?? -1}."
                              + (tail.Count > 0 ? "\n" + string.Join("\n", tail.TakeLast(10)) : "");
                await PersistWarmResultAsync(repo.Id, warmedHash: null, error: message, ct);
                status.RecordWarmFailure();
                logger.LogWarning("Toolcache warm for {Repo} failed: {Message}", repo.FullName, message);
            }
        } finally {
            await RemoveRunnerContainerAsync(container.Id, "warmer finished", ct);
        }
    }

    /// <summary>Persists a warm outcome: success stamps the warmed hash, failure records the error.</summary>
    private async Task PersistWarmResultAsync(int repoId, string? warmedHash, string? error, CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var now = DateTimeOffset.UtcNow;
        if (warmedHash is not null) {
            await db.CiRepos.Where(r => r.Id == repoId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.WarmedProfileHash, warmedHash)
                .SetProperty(r => r.LastWarmedAt, now)
                .SetProperty(r => r.LastWarmError, (string?)null), ct);
        } else {
            await db.CiRepos.Where(r => r.Id == repoId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.LastWarmError, error), ct);
        }
    }

    /// <summary>
    /// Best-effort deregistration for runners that never took a job (JIT ids are on the container
    /// labels). Returns true when the runner is known to be gone from GitHub — which doubles as
    /// proof that it was idle, since GitHub rejects the delete while a job is running.
    /// </summary>
    private async Task<bool> TryDeleteRegistrationAsync(CiRepo repo, DockerContainerInfo container, CancellationToken ct) {
        if (repo.Credential is null || !container.Labels.TryGetValue(RunnerIdLabel, out var idText) || !long.TryParse(idText, out var runnerId))
            return false;
        try {
            return await gitHub.TryDeleteRunnerAsync(repo.Owner, repo.Name, runnerId, repo.Credential.Token, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogDebug(ex, "Could not deregister runner {RunnerId} for {Repo}", runnerId, repo.FullName);
            return false;
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

/// <summary>
/// One runner container of a repo, as of the last reconcile pass. The orchestrator's own view of
/// what is on the host — Watchtower keeps no runner table in the database, so this is the only
/// place the individual containers are visible outside <c>docker ps</c>.
/// </summary>
/// <param name="Id">Short container id (12 chars), the form <c>docker</c> commands accept.</param>
/// <param name="Status">Docker's human-readable status line, e.g. "Up 3 minutes".</param>
/// <param name="GitHubRunnerId">
/// The runner's id at GitHub, for finding it under the repository's Actions runner settings. Null
/// for a container spawned before the id was labelled.
/// </param>
/// <param name="Stale">
/// The container was spawned under settings that have since changed. It keeps whatever job it is
/// running and is retired once idle — see <see cref="CiRunnerOrchestrator.SpecHashLabel"/>.
/// </param>
public sealed record CiRunnerContainer(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    DateTimeOffset? StartedAt,
    long? GitHubRunnerId,
    bool Stale);

/// <summary>Mutable per-repo runner state surfaced through <c>ci.getRunnerStatus</c>.</summary>
public sealed class CiRepoRunnerStatus {
    private int _consecutiveFailures;
    private DateTimeOffset? _warmBackoffUntil;

    public int DesiredRunners { get; private set; }
    public int RunningRunners { get; private set; }
    public long TotalSpawned { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastErrorAt { get; private set; }
    public DateTimeOffset? BackoffUntil { get; private set; }
    /// <summary>True while a toolcache warmer container for this repo is running.</summary>
    public bool WarmerRunning { get; private set; }

    /// <summary>
    /// The repo's runner containers as of the last reconcile pass — empty until the first one has
    /// run. Whole-list replacement rather than mutation, so a reader always sees one consistent
    /// pass rather than a half-updated table.
    /// </summary>
    public IReadOnlyList<CiRunnerContainer> Runners { get; private set; } = [];

    internal void UpdateRunners(IReadOnlyList<CiRunnerContainer> runners) => Runners = runners;

    /// <summary>
    /// Content stamp (<see cref="CiBuildkitConfig.Stamp"/>) of the buildkitd config last written by
    /// a successful volume init — which also chowned the cache volumes to the runner user. Null
    /// until the first init this orchestrator lifetime; a mismatch (registry list or snapshotter
    /// changed) re-runs the idempotent init. In-memory: a restart re-runs it once.
    /// </summary>
    internal string? VolumesReadyStamp { get; private set; }

    internal void MarkVolumesReady(string stamp) => VolumesReadyStamp = stamp;

    /// <summary>
    /// Earliest next Actions-config sync attempt after a failure (in-memory: a restart simply retries
    /// once). Keeps a persistently failing sync at one GitHub round-trip per interval, not per pass.
    /// </summary>
    /// <remarks>
    /// <b>One timer for both contributors</b>, deliberately. They authenticate with the same PAT and
    /// write through the same two GitHub permissions, so the failure that actually happens — the PAT
    /// was never granted Secrets/Variables write — fails both at once; two timers would double the
    /// round-trips that costs and give the UI two different answers to "when does this retry". The
    /// accepted consequence is that one failing contributor also parks the other for the window, which
    /// only ever delays a re-push the hash guard would otherwise have made immediately.
    /// </remarks>
    public DateTimeOffset? ActionsSyncRetryAt { get; private set; }

    internal void DeferActionsSyncRetry() => ActionsSyncRetryAt = DateTimeOffset.UtcNow.AddMinutes(5);

    internal void ClearActionsSyncRetry() => ActionsSyncRetryAt = null;

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

    internal void SetWarmerRunning(bool running) => WarmerRunning = running;

    /// <summary>
    /// Fixed 15-minute retry delay after a failed warm. In-memory only: a Watchtower restart retries
    /// immediately, which is fine — warm failures are cheap and usually transient download errors.
    /// </summary>
    internal void RecordWarmFailure() => _warmBackoffUntil = DateTimeOffset.UtcNow.AddMinutes(15);

    internal bool InWarmBackoff(DateTimeOffset now) => _warmBackoffUntil is { } until && until > now;
}
