using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Manages Watchtower's self-update lifecycle:
/// <list type="number">
///   <item>Auto-detects the image name from the running container (via the HOSTNAME environment variable).</item>
///   <item>Allows an optional registry credential for private images.</item>
///   <item>Checks for updates by comparing the remote manifest digest with the local one.</item>
///   <item>Applies updates by spawning a coordinator container that recreates this container via the Docker API.</item>
/// </list>
/// Nothing about the host layout needs to be configured: the coordinator clones the running
/// container's configuration onto the freshly pulled image, so no compose file is read or required.
/// Persisted state lives in the Elarion settings store as two Global-scope typed records — user
/// config under <c>self.config</c> (<see cref="SelfUpdateConfig"/>) and cached check + apply state
/// under <c>self.runtime</c> (<see cref="SelfUpdateRuntime"/>) — accessed through short-lived DI
/// scopes since this service is a singleton.
/// </summary>
public sealed class SelfUpdateService : IHostedService, IDisposable {
    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private const string KeyConfig = "self.config";
    private const string KeyRuntime = "self.runtime";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DockerEngineClient _docker;
    private readonly WatchtowerOptions _options;
    private readonly ILogger<SelfUpdateService> _logger;

    /// <summary>
    /// Ceiling on the startup reconcile. Startup has no other bound: the host is started with
    /// <c>CancellationToken.None</c> and <c>HostOptions.StartupTimeout</c> is left infinite, so the
    /// token handed to <see cref="StartAsync"/> never fires, and the coordinator wait it may run is
    /// on the untimed Docker client. Without this, a coordinator that is "running" but never exits
    /// (paused, wedged, host thrashing) would hold <c>IHost.StartAsync</c> open forever — the app
    /// never reaches Started, and SIGTERM cannot help because the shutdown signal does not reach
    /// the startup path. Giving up instead leaves the stage for a later reconcile, which is what
    /// happened before the wait moved off the 100-second default.
    /// </summary>
    internal static readonly TimeSpan StartupReconcileTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Ceiling on the live watch that follows spawning a coordinator. Longer than the startup one
    /// because this coordinator is doing the work — it sleeps ~3 s and then stops, renames, recreates
    /// and starts this container — where the startup reconcile only picks up someone else's
    /// leftovers. Bounded all the same, because this watch holds <c>_applyTask</c>, which is the
    /// apply mutex: a coordinator that never exits (its own stop call hanging on a sick daemon, say)
    /// would otherwise have every retry rejected with "already in progress" until the process
    /// restarts. Ten minutes is far past any healthy recreate; the happy path never reaches it,
    /// since the coordinator kills this process first.
    /// </summary>
    internal static readonly TimeSpan ApplyWatchTimeout = TimeSpan.FromMinutes(10);

    private readonly CancellationTokenSource _cts = new();
    private readonly object _applyLock = new();
    private readonly TimeSpan _startupReconcileTimeout;
    private readonly TimeSpan _applyWatchTimeout;
    private Task? _applyTask;

    public SelfUpdateService(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        IOptions<WatchtowerOptions> options,
        ILogger<SelfUpdateService> logger)
        : this(scopeFactory, docker, options, logger, StartupReconcileTimeout, ApplyWatchTimeout) { }

    /// <summary>Test seam: the two ceilings are injectable so a test need not wait out the real ones.</summary>
    internal SelfUpdateService(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        IOptions<WatchtowerOptions> options,
        ILogger<SelfUpdateService> logger,
        TimeSpan startupReconcileTimeout,
        TimeSpan applyWatchTimeout) {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _options = options.Value;
        _logger = logger;
        _startupReconcileTimeout = startupReconcileTimeout;
        _applyWatchTimeout = applyWatchTimeout;
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        // Reconcile any coordinator left behind by an apply that the previous process instance
        // never saw finish (the container was recreated mid-apply).
        var runtime = await LoadRuntimeAsync(cancellationToken);
        if (runtime.ApplyStage is not ("pulling" or "restarting")) return;

        if (runtime.CoordinatorId is null) {
            await SetStageAsync(SelfUpdateApplyStage.Idle, ct: cancellationToken);
            return;
        }

        // _cts is linked in so a stop request releases the reconcile too; neither token is
        // guaranteed to fire, which is why the wait inside carries a ceiling of its own.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await ReconcileCoordinatorAsync(runtime.CoordinatorId, _startupReconcileTimeout, linked.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        await _cts.CancelAsync();
        Task? running;
        lock (_applyLock) { running = _applyTask; }
        if (running is not null)
            await Task.WhenAny(running, Task.Delay(Timeout.Infinite, cancellationToken));
    }

    public void Dispose() {
        _cts.Dispose();
    }

    /// <summary>
    /// Waits for the coordinator container to exit and resolves the apply outcome: exit 0 clears the
    /// stage to Idle, any other exit surfaces the coordinator's logs as the apply error. Called both
    /// at startup (for a coordinator left behind by the previous process instance) and live right
    /// after spawning one — during a successful update the live call is cancelled when the
    /// coordinator recreates this container, and the next process instance finishes the job here.
    /// </summary>
    /// <param name="waitTimeout">
    /// Ceiling on the wait for the container to exit, and on that step alone. The bookkeeping that
    /// follows keeps running on <paramref name="ct"/>: a ceiling that could fire between clearing
    /// the stage and clearing the CoordinatorId would leave a runtime record no one ever revisits —
    /// <see cref="StartAsync"/> only reconciles a stage of "pulling"/"restarting" — with the
    /// coordinator container leaked as a stopped container for good.
    /// </param>
    internal async Task ReconcileCoordinatorAsync(string coordinatorId, TimeSpan waitTimeout, CancellationToken ct) {
        try {
            var details = await _docker.InspectContainerAsync(coordinatorId, ct);

            if (details.State?.Status == "running") {
                _logger.LogInformation("Coordinator {Id} is still running; waiting for it to exit", coordinatorId[..12]);
                if (!await TryWaitForExitAsync(coordinatorId, waitTimeout, ct)) return;
                details = await _docker.InspectContainerAsync(coordinatorId, ct);
            }

            var exitCode = details.State?.ExitCode ?? -1;
            var logs = await CollectCoordinatorLogsAsync(coordinatorId, ct);

            if (exitCode == 0) {
                _logger.LogInformation("Coordinator {Id} exited successfully — self-update applied", coordinatorId[..12]);
                await SetStageAsync(SelfUpdateApplyStage.Idle, ct: ct);
            } else {
                _logger.LogError("Coordinator {Id} exited with code {Code}:\n{Logs}", coordinatorId[..12], exitCode, logs);
                await SetStageAsync(SelfUpdateApplyStage.Error, $"Coordinator failed (exit {exitCode}):\n{logs.Trim()}", ct);
            }

            await _docker.RemoveContainerAsync(coordinatorId, ct);
            await UpdateRuntimeAsync(r => r with { CoordinatorId = null }, ct);
        } catch (OperationCanceledException) {
            // Shutting down — most likely because the coordinator just recreated this container.
            // Leave the stage and CoordinatorId in place for the next process instance to reconcile.
        } catch (Exception ex) {
            // Container not found (already removed) most likely means it ran and exited cleanly.
            _logger.LogDebug(ex, "Could not inspect coordinator container {Id}; assuming update completed", coordinatorId[..12]);
            await SetStageAsync(SelfUpdateApplyStage.Idle, ct: CancellationToken.None);
            await UpdateRuntimeAsync(r => r with { CoordinatorId = null }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Waits for the coordinator to exit under a ceiling of its own, since the wait itself is
    /// unbounded (the daemon holds the response until the container stops, on the untimed client).
    /// Returns false when the ceiling won — the caller then leaves the apply stage and CoordinatorId
    /// untouched for a later reconcile, exactly as a cancellation would.
    /// </summary>
    private async Task<bool> TryWaitForExitAsync(string coordinatorId, TimeSpan waitTimeout, CancellationToken ct) {
        // The ceiling gets its own source so "the ceiling won" is read off it directly rather than
        // inferred from ct, which a shutdown landing in the same instant would falsify.
        using var ceiling = new CancellationTokenSource(waitTimeout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, ceiling.Token);
        try {
            await _docker.WaitContainerAsync(coordinatorId, bounded.Token);
            return true;
        } catch (OperationCanceledException) when (ceiling.IsCancellationRequested) {
            _logger.LogWarning(
                "Coordinator {Id} had not exited after {Timeout}; leaving the apply stage to be reconciled later",
                coordinatorId[..12], waitTimeout);
            return false;
        }
    }

    private async Task<string> CollectCoordinatorLogsAsync(string containerId, CancellationToken ct) {
        try {
            var sb = new System.Text.StringBuilder();
            await foreach (var line in _docker.StreamLogsAsync(containerId, tail: 50, follow: false, ct))
                sb.AppendLine(line);
            return sb.ToString();
        } catch {
            return "(logs unavailable)";
        }
    }

    /// <summary>
    /// Inspects the running container (via HOSTNAME) to auto-detect the image, merges with the
    /// stored credential config, and returns the combined status.
    /// </summary>
    public async Task<SelfUpdateStatus> GetStatusAsync(CancellationToken ct = default) {
        var detected = await TryInspectSelfAsync(ct);
        var config = await LoadConfigAsync(ct);
        var liveCurrentDigest = await TryGetLocalDigestAsync(detected.ImageName, ct);
        var runtime = await LoadRuntimeAsync(ct);
        return BuildResponse(detected, config, runtime, liveCurrentDigest);
    }

    /// <summary>Persists the self-update configuration (registry credential).</summary>
    public async Task SaveConfigAsync(UpdateSelfConfig request, CancellationToken ct = default) {
        await SetConfigAsync(new SelfUpdateConfig { CredentialId = request.CredentialId }, ct);

        // Invalidate cached check result when config changes. A config change is also the
        // remediation path for a failed apply (e.g. fixing the registry credential), so clear a
        // lingering error state — but never touch an in-flight "pulling"/"restarting" stage.
        await UpdateRuntimeAsync(r => {
            var wasError = r.ApplyStage == "error";
            return r with {
                CurrentImageId = null,
                LatestImageId = null,
                IsOutdated = false,
                LastCheckedAt = null,
                ApplyStage = wasError ? "idle" : r.ApplyStage,
                ApplyError = wasError ? null : r.ApplyError,
            };
        }, ct);
    }

    /// <summary>
    /// Fetches the remote manifest digest of the detected image, compares it with the local
    /// image's digest, caches the result, and returns the updated status.
    /// </summary>
    /// <param name="acknowledgeApplyError">
    /// True for user-initiated checks, false for background ones — see
    /// <see cref="ApplyCheckResult"/> for why the two must differ.
    /// </param>
    /// <exception cref="InvalidOperationException">Thrown when no image name is available or the digest cannot be retrieved.</exception>
    public async Task<SelfUpdateStatus> CheckForUpdateAsync(bool acknowledgeApplyError, CancellationToken ct = default) {
        var detected = await TryInspectSelfAsync(ct);
        var config = await LoadConfigAsync(ct);
        var imageName = detected.ImageName;

        if (string.IsNullOrWhiteSpace(imageName))
            throw new InvalidOperationException(
                "No image name available. Ensure Watchtower is running as a Docker container.");

        var (username, token) = await ResolveCredentialAsync(config, ct);

        _logger.LogInformation("Checking self-update image digest for {Image}", imageName);
        var latestDigest = await _docker.GetRemoteDigestAsync(imageName, username, token, ct);

        if (string.IsNullOrWhiteSpace(latestDigest))
            throw new InvalidOperationException(
                $"Could not retrieve remote digest for image '{imageName}'. " +
                "The registry may not support the OCI Distribution Spec manifest endpoint, or the image does not exist.");

        // Inspect the local image by name to get RepoDigests (reliable across Docker versions).
        string? currentDigest = null;
        try {
            var localImage = await _docker.InspectImageAsync(imageName, ct);
            currentDigest = localImage.RepoDigests
                .Select(rd => rd.Contains('@') ? rd[(rd.IndexOf('@') + 1)..] : null)
                .FirstOrDefault(d => d is not null);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not inspect local image {Image} for digest comparison", imageName);
        }

        var isOutdated = currentDigest is not null && currentDigest != latestDigest;

        var runtime = await UpdateRuntimeAsync(
            r => ApplyCheckResult(r, currentDigest, latestDigest, isOutdated, acknowledgeApplyError, DateTimeOffset.UtcNow),
            ct);

        _logger.LogInformation(
            "Self-update check complete. CurrentDigest={Current}, LatestDigest={Latest}, IsOutdated={Outdated}",
            currentDigest, latestDigest, isOutdated);

        return BuildResponse(detected, config, runtime, liveCurrentDigest: currentDigest);
    }

    /// <summary>
    /// Folds a completed check into the runtime record. A user-initiated check
    /// (<paramref name="acknowledgeApplyError"/> true) also clears a lingering "error" apply stage:
    /// like a config change (see <see cref="SaveConfigAsync"/>), re-checking is how a user moves on
    /// from a failed apply, and without it the failure banner has no way to go away short of a
    /// successful update. Background checks pass false — they would wipe the banner on their next
    /// tick, before anyone had seen it. An in-flight "pulling"/"restarting" stage is never touched.
    /// </summary>
    internal static SelfUpdateRuntime ApplyCheckResult(
        SelfUpdateRuntime runtime, string? currentDigest, string? latestDigest, bool isOutdated,
        bool acknowledgeApplyError, DateTimeOffset checkedAt) {
        var clearError = acknowledgeApplyError && runtime.ApplyStage == "error";
        return runtime with {
            CurrentImageId = currentDigest,
            LatestImageId = latestDigest,
            IsOutdated = isOutdated,
            LastCheckedAt = checkedAt,
            ApplyStage = clearError ? "idle" : runtime.ApplyStage,
            ApplyError = clearError ? null : runtime.ApplyError,
        };
    }

    /// <summary>
    /// Validates that Watchtower is running as a container, then starts the slow pull +
    /// coordinator-spawn work as a tracked background task that also watches the coordinator's
    /// outcome. Returns as soon as validation passes. No further configuration is required — the
    /// coordinator recreates this container by cloning it via the Docker API.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when not running in a container or an apply is already in progress.</exception>
    public async Task ApplyUpdateAsync(string? actor = null, CancellationToken ct = default) {
        var detected = await TryInspectSelfAsync(ct);
        var config = await LoadConfigAsync(ct);

        if (!detected.IsRunningInContainer
            || string.IsNullOrWhiteSpace(detected.ImageName)
            || string.IsNullOrWhiteSpace(detected.ContainerId))
            throw new InvalidOperationException(
                "Self-update requires Watchtower to be running as a Docker container. Running outside Docker is not supported.");

        var (username, token) = await ResolveCredentialAsync(config, ct);

        // Guard against concurrent applies, then flip to "pulling" before releasing the lock.
        lock (_applyLock) {
            if (_applyTask is not null && !_applyTask.IsCompleted)
                throw new InvalidOperationException("A self-update is already in progress. Wait for the current pull to finish.");
            _applyTask = PullAndSpawnAsync(detected.ImageName, detected.ContainerId, username, token, actor, _cts.Token);
        }

        // The start is the only success this process can record — on a successful apply the
        // coordinator replaces the container before an outcome exists to write.
        await RecordAuditAsync("self-update.apply", detected.ImageName, "pull + container recreate started", actor: actor);
    }

    /// <summary>
    /// Records into the general audit trail. Resolved per call rather than injected: the audit
    /// recorder is DI-owned and this class has a hand-built test seam constructor.
    /// </summary>
    private async Task RecordAuditAsync(
        string action, string target, string? detail, bool success = true, string? error = null, string? actor = null) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditLog>()
            .RecordAsync("system", action, target, detail, success, error, actor);
    }

    private async Task PullAndSpawnAsync(
        string imageName, string containerId, string? username, string? token, string? actor, CancellationToken ct) {
        try {
            await SetStageAsync(SelfUpdateApplyStage.Pulling, ct: ct);

            _logger.LogInformation("Pulling image {Image} before self-update", imageName);
            await _docker.PullImageAsync(imageName, username, token, ct);
            _logger.LogInformation("Pull complete; spawning coordinator to recreate container {Id}", containerId[..12]);

            // Move to "restarting" and clear the stale check result so after the restart the UI
            // shows "Not yet checked".
            await UpdateRuntimeAsync(r => r with {
                ApplyStage = SelfUpdateApplyStage.Restarting.ToString().ToLowerInvariant(),
                ApplyError = null,
                CurrentImageId = null,
                LatestImageId = null,
                IsOutdated = false,
                LastCheckedAt = null,
            }, ct);

            var coordinatorName = $"watchtower-coordinator-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            // The coordinator runs from the just-pulled image, so it executes the newest code. It
            // only needs the Docker socket — the recreate is a pure Docker API operation.
            var coordinatorId = await _docker.CreateContainerAsync(new DockerCreateContainerBody {
                Image = imageName,
                Cmd = ["--self-update", "--container-id", containerId, "--image", imageName],
                Env = [$"WATCHTOWER__DOCKERAPIVERSION={_options.DockerApiVersion}"],
                HostConfig = new DockerCreateHostConfig {
                    Binds = ["/var/run/docker.sock:/var/run/docker.sock"],
                    NetworkMode = "none",
                    GroupAdd = GetCurrentGroupIds(),
                },
            }, coordinatorName, ct);

            await _docker.StartContainerAsync(coordinatorId, ct);
            await UpdateRuntimeAsync(r => r with { CoordinatorId = coordinatorId }, ct);

            _logger.LogInformation(
                "Coordinator container {Name} ({ShortId}) started; it will apply the update in ~3 s",
                coordinatorName, coordinatorId.Length >= 12 ? coordinatorId[..12] : coordinatorId);

            // Watch the coordinator so a failed (and rolled-back) recreate surfaces immediately
            // instead of sticking at "restarting" until the next restart. During a successful
            // update this wait is cancelled when the coordinator recreates this container;
            // ReconcileCoordinatorAsync swallows the cancellation and the next process instance
            // reconciles at startup. Ceiling-bounded because this task is the apply mutex — see
            // ApplyWatchTimeout.
            await ReconcileCoordinatorAsync(coordinatorId, _applyWatchTimeout, ct);
        } catch (OperationCanceledException) {
            _logger.LogWarning("Self-update pull/spawn was cancelled (host shutting down)");
            await SetStageAsync(SelfUpdateApplyStage.Error, "Update cancelled — host was shutting down.", CancellationToken.None);
        } catch (Exception ex) {
            _logger.LogError(ex, "Self-update background task failed");
            await SetStageAsync(SelfUpdateApplyStage.Error, ex.Message, CancellationToken.None);
            await RecordAuditAsync("self-update.apply", imageName, null, success: false, error: ex.Message, actor: actor);
        }
    }

    private Task SetStageAsync(SelfUpdateApplyStage stage, string? error = null, CancellationToken ct = default) =>
        UpdateRuntimeAsync(r => r with {
            ApplyStage = stage.ToString().ToLowerInvariant(),
            ApplyError = error,
        }, ct);

    private static string[] GetCurrentGroupIds() {
        try {
            foreach (var line in File.ReadLines("/proc/self/status")) {
                if (!line.StartsWith("Groups:", StringComparison.Ordinal)) continue;
                return ParseGroupsLine(line);
            }
        } catch {
            // Non-Linux or procfs unavailable — fall through and return empty.
        }
        return [];
    }

    /// <summary>
    /// The <c>Groups:</c> line separates the label from the ids with a tab and the ids with spaces
    /// (e.g. <c>"Groups:\t0 100 "</c>). Both must be treated as separators: an id that keeps a
    /// leading tab is no longer numeric to Docker, which then tries it as a group <em>name</em>
    /// against the image's <c>/etc/group</c> and fails the container start with
    /// "unable to find group 0: no matching entries in group file".
    /// </summary>
    internal static string[] ParseGroupsLine(string line) =>
        line["Groups:".Length..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    private async Task<DetectedSelfInfo> TryInspectSelfAsync(CancellationToken ct = default) {
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME") ?? "";
        if (string.IsNullOrWhiteSpace(hostname))
            return new DetectedSelfInfo();

        try {
            var details = await _docker.InspectContainerAsync(hostname, ct);
            return new DetectedSelfInfo {
                ContainerId = details.Id,
                ImageName = details.Config.Image,
                IsRunningInContainer = true,
            };
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not inspect self container via HOSTNAME={Hostname}", hostname);
            return new DetectedSelfInfo();
        }
    }

    private SelfUpdateStatus BuildResponse(
        DetectedSelfInfo detected, SelfUpdateConfig config, SelfUpdateRuntime runtime, string? liveCurrentDigest = null) {
        // Prefer the live digest (local image inspect, no registry call) so "Running" is always accurate.
        var currentImageId = liveCurrentDigest ?? runtime.CurrentImageId;

        var stage = Enum.TryParse<SelfUpdateApplyStage>(runtime.ApplyStage, ignoreCase: true, out var s)
            ? s
            : SelfUpdateApplyStage.Idle;

        return new SelfUpdateStatus {
            CredentialId = config.CredentialId,
            DetectedImageName = detected.ImageName,
            IsRunningInContainer = detected.IsRunningInContainer,
            CurrentImageId = currentImageId,
            LatestImageId = runtime.LatestImageId,
            IsOutdated = runtime.IsOutdated,
            LastCheckedAt = runtime.LastCheckedAt,
            CanApplyUpdate = detected.IsRunningInContainer && !string.IsNullOrWhiteSpace(detected.ImageName),
            ApplyStage = stage.ToString().ToLowerInvariant(),
            ApplyError = runtime.ApplyError,
            StartedAt = _startedAt,
        };
    }

    private async Task<string?> TryGetLocalDigestAsync(string? imageName, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(imageName)) return null;
        try {
            var localImage = await _docker.InspectImageAsync(imageName, ct);
            return localImage.RepoDigests
                .Select(rd => rd.Contains('@') ? rd[(rd.IndexOf('@') + 1)..] : null)
                .FirstOrDefault(d => d is not null);
        } catch {
            return null;
        }
    }

    /// <summary>Resolves the configured registry credential (username/token) for pulls, if any.</summary>
    private async Task<(string? Username, string? Token)> ResolveCredentialAsync(SelfUpdateConfig config, CancellationToken ct) {
        if (config.CredentialId is int credentialId) {
            var cred = await GetCredentialAsync(credentialId, ct);
            if (cred is not null) return cred.Value;
        }
        return (null, null);
    }

    // ── Scoped settings access ────────────────────────────────────────────────

    private async Task<SelfUpdateConfig> LoadConfigAsync(CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        return await mgr.GetAsync(KeyConfig, new SelfUpdateConfig(), SettingsScope.Global, ct);
    }

    private async Task SetConfigAsync(SelfUpdateConfig config, CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        await mgr.SetAsync(KeyConfig, config, SettingsScope.Global, expectedVersion: null, ct);
    }

    private async Task<SelfUpdateRuntime> LoadRuntimeAsync(CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        return await mgr.GetAsync(KeyRuntime, new SelfUpdateRuntime(), SettingsScope.Global, ct);
    }

    /// <summary>Read-modify-write the runtime record and return the new value (last-write-wins).</summary>
    private async Task<SelfUpdateRuntime> UpdateRuntimeAsync(
        Func<SelfUpdateRuntime, SelfUpdateRuntime> mutate, CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        var current = await mgr.GetAsync(KeyRuntime, new SelfUpdateRuntime(), SettingsScope.Global, ct);
        var updated = mutate(current);
        await mgr.SetAsync(KeyRuntime, updated, SettingsScope.Global, expectedVersion: null, ct);
        return updated;
    }

    private async Task<(string Username, string Token)?> GetCredentialAsync(int credentialId, CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.WatchtowerDbContext>();
        return await db.Credentials.AsNoTracking()
            .Where(c => c.Id == credentialId)
            .Select(c => new ValueTuple<string, string>(c.Username, c.Token))
            .Cast<(string, string)?>()
            .FirstOrDefaultAsync(ct);
    }

    private sealed record DetectedSelfInfo {
        public string? ContainerId { get; init; }
        public string? ImageName { get; init; }
        public bool IsRunningInContainer { get; init; }
    }
}
