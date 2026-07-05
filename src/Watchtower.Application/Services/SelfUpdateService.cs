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
///   <item>Auto-detects image name and compose config from the running container's Docker labels.</item>
///   <item>Allows optional manual overrides for image name, credential, and compose settings.</item>
///   <item>Checks for updates by comparing the remote manifest digest with the local one.</item>
///   <item>Applies updates by spawning a coordinator container that re-runs docker compose up -d.</item>
/// </list>
/// The running container is identified via the HOSTNAME environment variable. Persisted state lives
/// in the Elarion settings store as two Global-scope typed records — user overrides under
/// <c>self.config</c> (<see cref="SelfUpdateConfig"/>) and cached check + apply state under
/// <c>self.runtime</c> (<see cref="SelfUpdateRuntime"/>) — accessed through short-lived DI scopes
/// since this service is a singleton.
/// </summary>
public sealed class SelfUpdateService : IHostedService, IDisposable {
    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private const string KeyConfig = "self.config";
    private const string KeyRuntime = "self.runtime";

    private const string LabelComposeProject = "com.docker.compose.project";
    private const string LabelComposeConfigFiles = "com.docker.compose.project.config_files";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DockerEngineClient _docker;
    private readonly ComposeCliService _compose;
    private readonly WatchtowerOptions _options;
    private readonly ILogger<SelfUpdateService> _logger;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _applyLock = new();
    private Task? _applyTask;

    public SelfUpdateService(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        ComposeCliService compose,
        IOptions<WatchtowerOptions> options,
        ILogger<SelfUpdateService> logger) {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _compose = compose;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        // Reconcile any coordinator left behind by an apply that the previous process instance
        // never saw finish (the container was recreated mid-apply).
        var runtime = await LoadRuntimeAsync(cancellationToken);
        if (runtime.ApplyStage is "pulling" or "restarting")
            await ReconcileCoordinatorAsync(runtime, cancellationToken);
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

    private async Task ReconcileCoordinatorAsync(SelfUpdateRuntime runtime, CancellationToken ct) {
        var coordinatorId = runtime.CoordinatorId;
        if (coordinatorId is null) {
            await SetStageAsync(SelfUpdateApplyStage.Idle, ct: ct);
            return;
        }

        try {
            var details = await _docker.InspectContainerAsync(coordinatorId, ct);

            if (details.State?.Status == "running") {
                _logger.LogInformation("Coordinator {Id} is still running; waiting for it to exit", coordinatorId[..12]);
                await _docker.WaitContainerAsync(coordinatorId, ct);
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
        } catch (Exception ex) {
            // Container not found (already removed) most likely means it ran and exited cleanly.
            _logger.LogDebug(ex, "Could not inspect coordinator container {Id}; assuming update completed", coordinatorId[..12]);
            await SetStageAsync(SelfUpdateApplyStage.Idle, ct: ct);
        } finally {
            await UpdateRuntimeAsync(r => r with { CoordinatorId = null }, ct);
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
    /// Inspects the running container (via HOSTNAME) to auto-detect image and compose config,
    /// merges with stored manual overrides, and returns the combined status.
    /// </summary>
    public async Task<SelfUpdateStatus> GetStatusAsync(CancellationToken ct = default) {
        var detected = await TryInspectSelfAsync(ct);
        var config = await LoadConfigAsync(ct);
        var liveCurrentDigest = await TryGetLocalDigestAsync(config.ImageName ?? detected.ImageName, ct);
        var runtime = await LoadRuntimeAsync(ct);
        return BuildResponse(detected, config, runtime, liveCurrentDigest);
    }

    /// <summary>Persists manual override configuration. Pass null to clear an override and revert to auto-detection.</summary>
    public async Task SaveConfigAsync(UpdateSelfConfig request, CancellationToken ct = default) {
        var config = new SelfUpdateConfig {
            ImageName = string.IsNullOrWhiteSpace(request.ImageName) ? null : request.ImageName,
            CredentialId = request.CredentialId,
            ComposeFilePath = string.IsNullOrWhiteSpace(request.ComposeFilePath) ? null : request.ComposeFilePath,
            ComposeProjectName = string.IsNullOrWhiteSpace(request.ComposeProjectName) ? null : request.ComposeProjectName,
        };
        await SetConfigAsync(config, ct);

        // Invalidate cached check result when config changes.
        await UpdateRuntimeAsync(r => r with {
            CurrentImageId = null,
            LatestImageId = null,
            IsOutdated = false,
            LastCheckedAt = null,
        }, ct);
    }

    /// <summary>
    /// Fetches the remote manifest digest of the effective image, compares it with the local
    /// image's digest, caches the result, and returns the updated status.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no image name is available or the digest cannot be retrieved.</exception>
    public async Task<SelfUpdateStatus> CheckForUpdateAsync(CancellationToken ct = default) {
        var detected = await TryInspectSelfAsync(ct);
        var config = await LoadConfigAsync(ct);
        var effectiveImageName = config.ImageName ?? detected.ImageName;

        if (string.IsNullOrWhiteSpace(effectiveImageName))
            throw new InvalidOperationException(
                "No image name available. Set a manual override or ensure Watchtower is running as a Docker container.");

        var (username, token) = await ResolveCredentialAsync(config, ct);

        _logger.LogInformation("Checking self-update image digest for {Image}", effectiveImageName);
        var latestDigest = await _docker.GetRemoteDigestAsync(effectiveImageName, username, token, ct);

        if (string.IsNullOrWhiteSpace(latestDigest))
            throw new InvalidOperationException(
                $"Could not retrieve remote digest for image '{effectiveImageName}'. " +
                "The registry may not support the OCI Distribution Spec manifest endpoint, or the image does not exist.");

        // Inspect the local image by name to get RepoDigests (reliable across Docker versions).
        string? currentDigest = null;
        try {
            var localImage = await _docker.InspectImageAsync(effectiveImageName, ct);
            currentDigest = localImage.RepoDigests
                .Select(rd => rd.Contains('@') ? rd[(rd.IndexOf('@') + 1)..] : null)
                .FirstOrDefault(d => d is not null);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not inspect local image {Image} for digest comparison", effectiveImageName);
        }

        var isOutdated = currentDigest is not null && currentDigest != latestDigest;

        var runtime = await UpdateRuntimeAsync(r => r with {
            CurrentImageId = currentDigest,
            LatestImageId = latestDigest,
            IsOutdated = isOutdated,
            LastCheckedAt = DateTimeOffset.UtcNow,
        }, ct);

        _logger.LogInformation(
            "Self-update check complete. CurrentDigest={Current}, LatestDigest={Latest}, IsOutdated={Outdated}",
            currentDigest, latestDigest, isOutdated);

        return BuildResponse(detected, config, runtime, liveCurrentDigest: currentDigest);
    }

    /// <summary>
    /// Validates the compose configuration, then starts the slow pull + coordinator-spawn work
    /// as a tracked background task. Returns as soon as validation passes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when validation fails (not in a container, missing/invalid compose config).</exception>
    public async Task ApplyUpdateAsync(CancellationToken ct = default) {
        var detected = await TryInspectSelfAsync(ct);
        var config = await LoadConfigAsync(ct);
        var composeFilePath = config.ComposeFilePath ?? detected.ComposeFilePath;
        var composeProjectName = config.ComposeProjectName ?? detected.ComposeProjectName;

        if (string.IsNullOrWhiteSpace(composeFilePath) || string.IsNullOrWhiteSpace(composeProjectName))
            throw new InvalidOperationException(
                "Compose file path and project name are required but could not be auto-detected. " +
                "Set manual overrides in the self-update configuration.");

        if (!detected.IsRunningInContainer || string.IsNullOrWhiteSpace(detected.ImageName))
            throw new InvalidOperationException(
                "Self-update requires Watchtower to be running as a Docker container. Running outside Docker is not supported.");

        if (!File.Exists(composeFilePath))
            throw new InvalidOperationException(
                $"Compose file not found at '{composeFilePath}' inside the Watchtower container. " +
                $"Mount the directory into the container, for example: " +
                $"-v {Path.GetDirectoryName(composeFilePath)}:{Path.GetDirectoryName(composeFilePath)}:ro");

        var (exitCode, output) = await _compose.ConfigAsync(composeFilePath, composeProjectName, ct);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Compose file validation failed (docker compose config exited {exitCode}):\n{output.Trim()}");

        var (username, token) = await ResolveCredentialAsync(config, ct);

        // Guard against concurrent applies, then flip to "pulling" before releasing the lock.
        lock (_applyLock) {
            if (_applyTask is not null && !_applyTask.IsCompleted)
                throw new InvalidOperationException("A self-update is already in progress. Wait for the current pull to finish.");
            _applyTask = PullAndSpawnAsync(detected, composeFilePath, composeProjectName, username, token, _cts.Token);
        }
    }

    private async Task PullAndSpawnAsync(
        DetectedSelfInfo detected, string composeFilePath, string composeProjectName,
        string? username, string? token, CancellationToken ct) {
        try {
            await SetStageAsync(SelfUpdateApplyStage.Pulling, ct: ct);

            _logger.LogInformation("Pulling image {Image} before self-update", detected.ImageName);
            await _docker.PullImageAsync(detected.ImageName!, username, token, ct);
            _logger.LogInformation("Pull complete; spawning coordinator: project {Project} at {File}", composeProjectName, composeFilePath);

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

            var composeDir = Path.GetDirectoryName(composeFilePath)!;
            var coordinatorName = $"watchtower-coordinator-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            var containerId = await _docker.CreateContainerAsync(new DockerCreateContainerBody {
                Image = detected.ImageName!,
                Cmd = ["--self-update", "--compose-file", composeFilePath, "--project-name", composeProjectName],
                Env = [$"WATCHTOWER__DOCKERAPIVERSION={_options.DockerApiVersion}"],
                HostConfig = new DockerCreateHostConfig {
                    Binds = [
                        "/var/run/docker.sock:/var/run/docker.sock",
                        $"{composeDir}:{composeDir}:ro",
                    ],
                    NetworkMode = "none",
                    GroupAdd = GetCurrentGroupIds(),
                },
            }, coordinatorName, ct);

            await _docker.StartContainerAsync(containerId, ct);
            await UpdateRuntimeAsync(r => r with { CoordinatorId = containerId }, ct);

            _logger.LogInformation(
                "Coordinator container {Name} ({ShortId}) started; it will apply the update in ~3 s",
                coordinatorName, containerId.Length >= 12 ? containerId[..12] : containerId);
        } catch (OperationCanceledException) {
            _logger.LogWarning("Self-update pull/spawn was cancelled (host shutting down)");
            await SetStageAsync(SelfUpdateApplyStage.Error, "Update cancelled — host was shutting down.", CancellationToken.None);
        } catch (Exception ex) {
            _logger.LogError(ex, "Self-update background task failed");
            await SetStageAsync(SelfUpdateApplyStage.Error, ex.Message, CancellationToken.None);
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
                return line[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }
        } catch {
            // Non-Linux or procfs unavailable — fall through and return empty.
        }
        return [];
    }

    private async Task<DetectedSelfInfo> TryInspectSelfAsync(CancellationToken ct = default) {
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME") ?? "";
        if (string.IsNullOrWhiteSpace(hostname))
            return new DetectedSelfInfo();

        try {
            var details = await _docker.InspectContainerAsync(hostname, ct);
            var labels = details.Config.Labels;

            var composeConfigFiles = labels.GetValueOrDefault(LabelComposeConfigFiles);
            var detectedComposePath = composeConfigFiles?.Split(',').FirstOrDefault()?.Trim();
            var detectedProjectName = labels.GetValueOrDefault(LabelComposeProject);

            return new DetectedSelfInfo {
                ImageName = details.Config.Image,
                ComposeFilePath = detectedComposePath,
                ComposeProjectName = detectedProjectName,
                IsRunningInContainer = true,
            };
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not inspect self container via HOSTNAME={Hostname}", hostname);
            return new DetectedSelfInfo();
        }
    }

    private SelfUpdateStatus BuildResponse(
        DetectedSelfInfo detected, SelfUpdateConfig config, SelfUpdateRuntime runtime, string? liveCurrentDigest = null) {
        var effectiveComposePath = config.ComposeFilePath ?? detected.ComposeFilePath;
        var effectiveProjectName = config.ComposeProjectName ?? detected.ComposeProjectName;

        // Prefer the live digest (local image inspect, no registry call) so "Running" is always accurate.
        var currentImageId = liveCurrentDigest ?? runtime.CurrentImageId;

        var stage = Enum.TryParse<SelfUpdateApplyStage>(runtime.ApplyStage, ignoreCase: true, out var s)
            ? s
            : SelfUpdateApplyStage.Idle;

        return new SelfUpdateStatus {
            ImageName = config.ImageName,
            CredentialId = config.CredentialId,
            ComposeFilePath = config.ComposeFilePath,
            ComposeProjectName = config.ComposeProjectName,
            DetectedImageName = detected.ImageName,
            DetectedComposeFilePath = detected.ComposeFilePath,
            DetectedComposeProjectName = detected.ComposeProjectName,
            IsRunningInContainer = detected.IsRunningInContainer,
            CurrentImageId = currentImageId,
            LatestImageId = runtime.LatestImageId,
            IsOutdated = runtime.IsOutdated,
            LastCheckedAt = runtime.LastCheckedAt,
            CanApplyUpdate = !string.IsNullOrWhiteSpace(effectiveComposePath)
                             && !string.IsNullOrWhiteSpace(effectiveProjectName),
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
        public string? ImageName { get; init; }
        public string? ComposeFilePath { get; init; }
        public string? ComposeProjectName { get; init; }
        public bool IsRunningInContainer { get; init; }
    }
}
