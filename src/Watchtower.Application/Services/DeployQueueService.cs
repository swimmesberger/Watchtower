using System.Collections.Concurrent;
using System.Globalization;
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

/// <summary>Outcome of enqueuing a deploy: the tracking event id and its current status.</summary>
public sealed record DeployEnqueueResult(int DeployEventId, string Status);

/// <summary>
/// Manages per-stack deploy queues with smart coalescing.
/// At most one deploy runs per stack at a time, with one pending slot.
/// If a third request arrives for the same stack the caller receives the existing
/// pending event id back (coalesced), avoiding redundant work.
///
/// Across stacks, at most <c>Watchtower:MaxConcurrentDeploys</c> deploys run at once
/// (<see cref="ExecuteDeployAsync"/> takes <see cref="_deployGate"/> before it starts working).
///
/// Registered as a singleton (so RPC handlers and the webhook endpoint can enqueue work) and
/// as an <see cref="IHostedService"/> for graceful shutdown. All database access is performed
/// through short-lived scopes resolved from <see cref="IServiceScopeFactory"/> because the
/// singleton must not capture a scoped <see cref="WatchtowerDbContext"/>.
/// </summary>
public class DeployQueueService : IHostedService, IDisposable {
    private readonly ConcurrentDictionary<int, StackSlot> _slots = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// The cross-stack concurrency gate. One worker per stack with no global cap makes a bulk action
    /// (<c>templates.deployAll</c>, later a release fan-out) point every tenant's clone, pull and
    /// <c>up</c> at one registry and one Docker daemon simultaneously; this bounds how many run at
    /// once. Per-stack queueing above it is untouched — a gated deploy simply waits its turn, and its
    /// tracking row still says <c>queued</c> while it does.
    /// </summary>
    /// <remarks>
    /// Instance state on a service the host registers as a singleton, so it is process-wide where it
    /// matters and a test can still stand up an isolated queue. Sized once here rather than per deploy:
    /// the permits are held across whole deploys, so swapping the semaphore for a resized one would let
    /// more than the new limit run until the deploys holding the old one finished.
    /// </remarks>
    private readonly SemaphoreSlim _deployGate;

    /// <summary>The size of <see cref="_deployGate"/>, kept for the line a waiting deploy prints.</summary>
    private readonly int _maxConcurrentDeploys;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GitCloneService _git;
    private readonly ComposeCliService _compose;
    private readonly DockerEngineClient _docker;
    private readonly DeployOutputBroadcaster _broadcaster;
    private readonly IProxyProvider _proxy;
    private readonly IOptionsMonitor<WatchtowerOptions> _options;
    private readonly ILogger<DeployQueueService> _logger;

    /// <param name="scopeFactory">Creates the short-lived scopes all database access runs in.</param>
    /// <param name="git">Repository clone helper.</param>
    /// <param name="compose">Docker Compose CLI wrapper.</param>
    /// <param name="docker">Docker Engine API client.</param>
    /// <param name="broadcaster">Live deploy-output fan-out for the SSE stream.</param>
    /// <param name="caddy">Reverse-proxy reconciler invoked after a successful deploy.</param>
    /// <param name="options">
    /// Live Watchtower options; <c>PublicBaseUrl</c> is read per deploy to decide whether
    /// <c>WATCHTOWER_URL</c> is injected into the stack's environment, and
    /// <c>MaxConcurrentDeploys</c> once here to size <see cref="_deployGate"/>.
    /// </param>
    /// <param name="logger">Logger.</param>
    public DeployQueueService(
        IServiceScopeFactory scopeFactory,
        GitCloneService git,
        ComposeCliService compose,
        DockerEngineClient docker,
        DeployOutputBroadcaster broadcaster,
        IProxyProvider proxy,
        IOptionsMonitor<WatchtowerOptions> options,
        ILogger<DeployQueueService> logger) {
        _scopeFactory = scopeFactory;
        _git = git;
        _compose = compose;
        _docker = docker;
        _broadcaster = broadcaster;
        _proxy = proxy;
        _options = options;
        _logger = logger;
        _maxConcurrentDeploys = options.CurrentValue.ResolveMaxConcurrentDeploys();
        _deployGate = new SemaphoreSlim(_maxConcurrentDeploys, _maxConcurrentDeploys);
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

    /// <remarks>
    /// <see cref="_deployGate"/> is deliberately not disposed. Dispose runs while deploys may still be
    /// executing, and a disposed semaphore turns their <c>Release()</c> into an
    /// <see cref="ObjectDisposedException"/> thrown out of the cleanup block — which would skip the
    /// deletion of the generated env file, and that file carries the stack's App API token. A
    /// <see cref="SemaphoreSlim"/> holds nothing to dispose unless its <c>AvailableWaitHandle</c> has
    /// been used, and this one's never is.
    /// </remarks>
    public void Dispose() => _cts.Dispose();

    /// <summary>
    /// Enqueues a deploy for <paramref name="stackId"/>. Returns the deploy event that will track it.
    /// </summary>
    /// <remarks>
    /// Three outcomes: <c>running</c> (worker started immediately), <c>queued</c> (a deploy is running,
    /// this request was stored as next-to-run), or coalesced (a deploy is running AND one is already
    /// pending — the caller receives the existing pending event id, no new row created).
    /// <para>
    /// <c>running</c> is about this <em>stack's</em> queue: it means nothing was ahead of this request,
    /// so a worker took it straight away. The worker may still wait at the cross-stack gate, which is
    /// why the tracking row is left saying <c>queued</c> until <see cref="ExecuteDeployAsync"/> has a
    /// permit and genuinely starts.
    /// </para>
    /// </remarks>
    /// <param name="removeVolumes">
    /// Optional list of named volumes to delete (via <c>compose down</c> → <c>docker volume rm</c>)
    /// after clone and BEFORE pull/up — the <c>volumes.recreate</c> data-wipe flow. Null/empty for a
    /// plain deploy. This payload is threaded through the running AND pending slots: if a recreate is
    /// coalesced onto a pending plain deploy (or vice-versa), the pending slot keeps the UNION of the
    /// volume lists so a recreate is never silently downgraded to a plain deploy.
    /// </param>
    /// <remarks>
    /// Coalescing merges <em>upwards</em> in both dimensions — volumes and trigger. The trigger rule
    /// exists because a pending slot's trigger decides whether the deploy that eventually runs may
    /// short-circuit: an operator's <c>manual</c> coalesced onto a pending <c>release</c> must run the
    /// full pipeline, or the deploy button would report success having done nothing. See
    /// <see cref="DeployTriggers.MayShortCircuit"/>.
    /// </remarks>
    /// <remarks>
    /// Virtual, and the class is not sealed, so a test host can accept work without spawning a worker:
    /// the worker clones a repository and shells out to compose, neither of which exists in a test, and
    /// it would do so on a background thread racing the test's own database connection.
    /// </remarks>
    public virtual DeployEnqueueResult Enqueue(
        int stackId, string triggeredBy, IReadOnlyList<string>? removeVolumes = null) {
        var slot = _slots.GetOrAdd(stackId, _ => new StackSlot());

        lock (slot.Lock) {
            if (!slot.IsRunning) {
                var eventId = CreateEvent(stackId, triggeredBy);
                UpdateDeployStatus(stackId, DeployStatus.Queued);
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
            // Two merge rules, both of the same shape: the coalesced request must never come out
            // weaker than either request that went in.
            //
            // 1. Volumes: the pending slot keeps the UNION of the two lists, so a plain deploy landing
            //    on a pending recreate cannot silently drop the data wipe.
            // 2. Trigger: a trigger that may NOT short-circuit supersedes one that may. Without this, an
            //    operator's "manual" landing on a pending "release" would run as a release deploy and
            //    no-op away — the deploy button doing nothing, with a success to show for it. The
            //    reverse ("release" onto a pending "manual") keeps "manual", because a full deploy is
            //    never the wrong answer to a fan-out.
            if (!DeployTriggers.MayShortCircuit(triggeredBy))
                slot.PendingTriggeredBy = triggeredBy;
            var merged = UnionVolumes(slot.PendingRemoveVolumes, removeVolumes);
            slot.PendingRemoveVolumes = merged;
            // Last, so it outranks the trigger rule above: a recreate is the strongest request there is.
            if (merged is { Count: > 0 })
                slot.PendingTriggeredBy = DeployTriggers.VolumeRecreate;
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
            await ExecuteDeployAsync(stackId, eventId, triggeredBy, removeVolumes, ct);

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
    /// Runs the full clone → [down + volume-rm] → config → pull → up pipeline for one deploy event.
    /// When <paramref name="removeVolumes"/> is non-empty, the stack is brought down and each named
    /// volume is deleted after the clone and before pull/up (the data-wipe recreate flow).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal rather than private so a test can drive one deploy to completion on its own thread. The
    /// public entry point is <see cref="Enqueue"/>, which starts a background worker — awaiting a
    /// deploy through it would mean racing that worker for the test's single SQLite connection.
    /// </para>
    /// <para>
    /// The cross-stack gate is taken here, not around the worker loop, so that every path into a deploy
    /// is bounded by it and the event's <c>running</c> transition happens on the far side of the wait:
    /// a deploy queued behind the gate reads as queued, which is what it is. A deploy that actually had
    /// to wait re-reads its stack afterwards, because the wait is unbounded and nothing cancels a
    /// parked deploy when the stack is stopped, repointed or deleted underneath it.
    /// </para>
    /// <para>
    /// <b>Releases (ADR-0026) enter here and nowhere earlier.</b> The target release is resolved from
    /// the stack read on the far side of the gate — the same staleness rule the stopped-stack re-check
    /// follows, so a pin written or a mode reverted while the deploy was parked is honoured. Everything
    /// release-shaped below is gated on <c>ReleaseMode == Releases</c>: for a product in <c>Git</c> mode
    /// <see cref="ReleaseResolver"/> answers null before it queries anything, and the clone, the
    /// override and the recorded outcome are byte-for-byte what they were before releases existed.
    /// </para>
    /// </remarks>
    /// <param name="stackId">The stack to deploy.</param>
    /// <param name="eventId">The tracking <see cref="DeployEvent"/>.</param>
    /// <param name="triggeredBy">
    /// What asked for this deploy (<see cref="DeployTriggers"/>). Passed down rather than read back off
    /// the event row because coalescing can supersede a pending event's trigger without rewriting it,
    /// and the short-circuit below must key on the trigger this run actually carries.
    /// </param>
    /// <param name="removeVolumes">Named volumes to delete before pull/up; null for a plain deploy.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task ExecuteDeployAsync(
        int stackId, int eventId, string triggeredBy, IReadOnlyList<string>? removeVolumes,
        CancellationToken ct) {
        var stack = GetStack(stackId);
        if (stack is null) {
            CompleteEvent(eventId, "failed", "[Watchtower] Stack not found — it may have been deleted.");
            return;
        }

        // Backstop for deploys enqueued before (or racing) a stacks.stop: a stopped stack is
        // deliberately disabled (ADR-0025) and a deploy would bring its containers back up. Checked
        // again on the far side of the concurrency gate, where an arbitrary amount of time may have
        // passed since this read.
        if (stack.DesiredState == StackDesiredState.Stopped) {
            CompleteEvent(eventId, "failed", "[Watchtower] Stack is stopped — start it before deploying.");
            UpdateDeployStatus(stackId, DeployStatus.Failed);
            return;
        }

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
        string? overrideFilePath = null;
        var gateHeld = false;

        try {
            // 0. Wait for a slot in the instance-wide deploy gate. Inside the try so a shutdown while
            //    waiting is reported like any other cancelled deploy, and so the permit is released by
            //    the same finally that cleans everything else up.
            if (!_deployGate.Wait(0)) {
                WriteHeader(
                    $"[Watchtower] Waiting for a deploy slot ({_maxConcurrentDeploys} deploys may run at once)");
                UpdateOutput(eventId, output.ToString());
                await _deployGate.WaitAsync(ct);
                gateHeld = true;

                // The wait can last minutes, and nothing cancels a parked deploy: stacks.stop only
                // writes the desired state (ADR-0025), and stacks.update only writes the repository
                // fields. The snapshot taken before the wait is therefore stale, and acting on it would
                // bring a stack the operator stopped back up, or deploy the repository it just
                // repointed away from. Re-read it and re-apply the same two refusals.
                stack = GetStack(stackId);
                if (stack is null) {
                    WriteHeader("[Watchtower] Stack not found — it may have been deleted.");
                    CompleteEvent(eventId, "failed", output.ToString());
                    return;
                }
                if (stack.DesiredState == StackDesiredState.Stopped) {
                    WriteHeader("[Watchtower] Stack is stopped — start it before deploying.");
                    CompleteEvent(eventId, "failed", output.ToString());
                    UpdateDeployStatus(stackId, DeployStatus.Failed);
                    return;
                }
            } else {
                gateHeld = true;
            }

            // 0b. Which release is this deploy of? Null for a Git-mode product — the mode check happens
            //     at this call site, so a Git-mode deploy does not even open a scope. Resolved from the
            //     post-gate stack read, so a pin or a mode change that landed during the wait is the one
            //     that applies — and resolved BEFORE the deploy is marked running, so a short-circuited
            //     one never claims to have started.
            var release = ReleaseResolver.UsesReleases(ReleaseResolver.RequireProduct(stack))
                ? await ResolveReleaseAsync(stack, ct)
                : null;
            if (release is not null) {
                // Stamped as soon as it is known, so even a deploy that fails at the clone reports which
                // release it was trying to apply.
                RecordEventRelease(eventId, release.Id);

                // The convergence short-circuit (design.md §Convergent fan-out): a fan-out only ever
                // asks a stack to reach the newest release, so one that is already there has nothing to
                // do. Restricted to the "release" trigger — every other one converges compose, env and
                // configuration too — and to a plain deploy, because a coalesced volume recreate must
                // never be swallowed by it.
                //
                // Recorded on the event and NOWHERE else — which is why it is decided before
                // MarkRunning below. The event is terminal and successful, because "nothing to do" is a
                // successful outcome for the fan-out that asked. But Stack.LastDeployedAt and
                // Stack.LastDeployStatus describe the last deploy that actually ran, and a stack's
                // "deployed 2 minutes ago" must not start meaning "a release arrived and we decided it
                // was already there" — otherwise every tenant of a product would show a fresh deploy
                // timestamp after every release, whether or not anything was deployed to it. The event
                // never passes through `running` either, for the same reason: it never ran.
                if (DeployTriggers.MayShortCircuit(triggeredBy)
                    && removeVolumes is null or { Count: 0 }
                    && stack.LastDeployedReleaseId == release.Id) {
                    WriteHeader($"[Watchtower] Already on {release.Version} — nothing to do.");
                    CompleteEvent(eventId, "success", output.ToString());
                    return;
                }
            }

            MarkRunning(eventId);
            UpdateDeployStatus(stackId, DeployStatus.Running);

            // 1. Resolve the effective source (product + overrides, ADR-0026) and its git credential.
            var source = ProductSourceResolver.Resolve(stack);
            var gitToken = source.CredentialId is { } credId ? GetCredentialToken(credId) : null;

            if (release is not null) {
                WriteHeader(release.CommitSha is { } sha
                    ? $"[Watchtower] Deploying release {release.Version} (commit {sha[..8]})"
                    : $"[Watchtower] Deploying release {release.Version} "
                      + $"(no commit recorded — deploying the head of '{source.Branch}')");
            }

            // 2. Clone the repository — at the release's commit when it recorded one, so the compose
            //    file, entrypoints and migrations of a rollback travel back with the images. A release
            //    without a commit still pins its digests; only the checkout falls back to the branch.
            WriteHeader($"[Watchtower] Cloning {source.RepositoryUrl} @ {source.Branch}");
            var cloneResult = release?.CommitSha is { } commitSha
                ? await _git.CloneAtCommitAsync(
                    source.RepositoryUrl, source.Branch, commitSha, gitToken, tempRepoDir, OnSubprocessLine, ct)
                : await _git.CloneAsync(
                    source.RepositoryUrl, source.Branch, gitToken, tempRepoDir, OnSubprocessLine, ct);
            output.Append(cloneResult.Output);
            UpdateOutput(eventId, output.ToString());
            if (cloneResult.ExitCode != 0) {
                CompleteEvent(eventId, "failed", output.ToString());
                UpdateDeployStatus(stackId, DeployStatus.Failed);
                return;
            }

            // Record which commit this deploy runs so pull-based checks can compare against it later.
            var deployedCommit = await _git.GetHeadCommitAsync(tempRepoDir, ct);
            if (deployedCommit is not null)
                WriteHeader($"[Watchtower] Checked out commit {deployedCommit[..8]}");

            // 2a. CI toolchain detection piggybacks on the clone (docs/ci-runners/design.md): when
            //     this repository has CI runners enabled, refresh its detected toolchain profile from
            //     the working tree. Best-effort by contract — the recorder swallows its own failures.
            if (await RecordCiToolchainsAsync(stack.ProductId, tempRepoDir, ct) is { } toolchainSummary)
                WriteHeader($"[Watchtower] {toolchainSummary}");

            // TrimStart ensures an accidentally absolute path is treated as relative to the cloned repo root.
            var composePath = Path.Combine(tempRepoDir, source.ComposeFilePath.TrimStart('/', '\\'));

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
            dockerConfigDir = await CreateRegistryConfigDirAsync(ct);

            // 4. Materialize the temp .env consumed by `docker compose --env-file`. Since Watchtower
            //    always injects reserved variables, --env-file is always passed — and passing it makes
            //    Compose v2 STOP auto-loading the project directory's own .env. The repo's committed
            //    .env is therefore merged in explicitly here; without that, a stack whose repository
            //    ships a .env would silently interpolate every one of those variables to empty on its
            //    next deploy. See BuildEnvFileContent for the precedence rules.
            var envVars = GetEnvVars(stackId);
            var appApiToken = await EnsureAppApiTokenAsync(stackId, ct);
            // Read once: the .env and the override below must agree, and IOptionsMonitor is live.
            var optionsSnapshot = _options.CurrentValue;
            var publicBaseUrl = optionsSnapshot.PublicBaseUrl;
            // Which JWKS the active edge signs identity assertions with (Cloudflare Access or
            // Watchtower's own) — injected so apps verify without hard-coding an issuer.
            var authJwksUrl = AppApiTokens.ResolveJwksUrl(optionsSnapshot);
            var reservedVars = BuildReservedEnvVars(stackId, appApiToken, publicBaseUrl, authJwksUrl);
            var repoEnv = await ReadRepoEnvEntriesAsync(composePath, ct);
            foreach (var droppedKey in repoEnv.DroppedKeys)
                WriteHeader($"[Watchtower] Warning: dropped malformed .env entry '{droppedKey}' (unterminated quote)");

            envFilePath = Path.Combine(Path.GetTempPath(), $"watchtower-env-{Guid.NewGuid():N}.env");
            var (envContent, operatorCount, repoCount) =
                BuildEnvFileContent(repoEnv.Entries, envVars, reservedVars);
            await File.WriteAllTextAsync(envFilePath, envContent, ct);
            // The file now always carries WATCHTOWER_APP_TOKEN, so keep it owner-only rather than
            // leaving it at the process umask in a world-readable temp directory.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(envFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            WriteHeader(
                $"[Watchtower] Injecting {operatorCount} environment variable(s), "
                + $"{repoCount} carried over from the repository .env, "
                + $"reserved: {string.Join(", ", reservedVars.Select(v => v.Key))}");

            // 4b. Generate the compose override that puts the reserved variables directly into the
            //     services' `environment:`. Without it they would only be available for interpolation,
            //     so a repository that forgot to pass them through would deploy fine and then fail
            //     every App API call with a 401 that points at nothing. See ADR-0012.
            var configResult = await _compose.ConfigJsonAsync(
                composePath, stack.ComposeProjectName, envFilePath, ct);
            if (configResult.ExitCode != 0) {
                // The same file is about to be handed to pull/up, so this would have failed there
                // anyway — failing here just reports it against the step that actually noticed.
                WriteHeader("[Watchtower] Reading the compose project failed:");
                // Line by line so the live stream carries the reason too, not just the headline. Only
                // Compose's stderr is echoed: its stdout is the resolved project, values and all.
                foreach (var line in configResult.Diagnostics.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    WriteHeader(line.TrimEnd('\r'));
                CompleteEvent(eventId, "failed", output.ToString());
                UpdateDeployStatus(stackId, DeployStatus.Failed);
                return;
            }

            // One parse of the resolved project, two policies over it: the engine reads the services
            // once and no two readers can disagree about what it said.
            var services = ComposeOverrideFile.ParseServices(configResult.Json);
            var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
                services,
                stackId,
                appApiToken,
                publicBaseUrl,
                GetTemplateTargetService(stack.TemplateId),
                authJwksUrl));
            foreach (var warning in plan.Warnings)
                WriteHeader($"[Watchtower] {warning}");

            // 4c. Image pinning (ADR-0026 decision 6), and only in Releases mode: null here is what
            //     makes Render produce exactly the document it produced before this stage.
            ImagePinPlan? imagePlan = null;
            if (release is not null) {
                imagePlan = ImagePinPlan.Create(
                    services,
                    [.. release.Images.Select(i => new ReleaseImageRef(i.Repository, i.Digest))]);
                foreach (var warning in imagePlan.Warnings)
                    WriteHeader($"[Watchtower] {warning}");
            }

            // 4d. Device mappings (ADR-0030): host devices configured per stack in the UI, rendered
            //     into the same generated override — host-specific values the repository's compose
            //     file must not carry. Empty for a stack with no rows, which keeps the override
            //     byte-identical to its pre-device form.
            var devicePlan = DeviceMappingPlan.Create(services, GetDeviceMappings(stackId));
            foreach (var warning in devicePlan.Warnings)
                WriteHeader($"[Watchtower] {warning}");

            if (ComposeOverrideFile.Render(plan, imagePlan, devicePlan) is { } overrideContent) {
                overrideFilePath = Path.Combine(
                    Path.GetTempPath(), $"watchtower-override-{Guid.NewGuid():N}.yml");
                await File.WriteAllTextAsync(overrideFilePath, overrideContent, ct);
                // It may carry the App API token, so it gets the same treatment as the generated .env.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(overrideFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                // Digests are not secrets, so the pin lines name them in full — "which build is this
                // stack actually running" has to be answerable from the deploy log alone.
                foreach (var pin in imagePlan?.Services ?? [])
                    WriteHeader($"[Watchtower] Pinning service '{pin.ServiceName}' to {pin.Image}");
                foreach (var service in plan.Services)
                    WriteHeader(
                        $"[Watchtower] Injecting {string.Join(", ", service.Variables.Select(v => v.Key))} "
                        + $"into service '{service.ServiceName}'");
                // One line per device: mapping a host device into a container is an operator-level
                // grant, so "why does this container see the GPU" must be answerable from the log.
                foreach (var mapped in devicePlan.Services)
                    foreach (var device in mapped.Devices)
                        WriteHeader(
                            $"[Watchtower] Mapping device {device.HostPath} into service "
                            + $"'{mapped.ServiceName}' at {device.ContainerPath}"
                            + (device.Permissions is { } permissions ? $" ({permissions})" : ""));
            }

            // 5. Pull updated images.
            WriteHeader($"[Watchtower] Pulling images for project '{stack.ComposeProjectName}'");
            UpdateOutput(eventId, output.ToString());
            var pullResult = await _compose.PullAsync(
                composePath, stack.ComposeProjectName, dockerConfigDir, envFilePath, overrideFilePath,
                OnSubprocessLine, ct);
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
                composePath, stack.ComposeProjectName, dockerConfigDir, envFilePath, overrideFilePath,
                OnSubprocessLine, ct);
            output.Append(upResult.Output);

            var finalStatus = upResult.ExitCode == 0 ? "success" : "failed";
            CompleteEvent(eventId, finalStatus, output.ToString());
            UpdateDeployStatus(stackId, upResult.ExitCode == 0 ? DeployStatus.Success : DeployStatus.Failed);
            // A successful deploy may have pulled new images; clear the cached check and remember
            // the deployed commit as the new baseline for git-based update detection.
            if (upResult.ExitCode == 0) {
                DeleteUpdateCheck(stackId);
                if (deployedCommit is not null)
                    RecordDeployedCommit(stackId, deployedCommit);
                // What this stack is now on. Written from the release resolved at execution time, so a
                // coalesced deploy records what ran rather than what was asked for — the value the
                // short-circuit, the scheduled window and the update check all compare against.
                if (release is not null)
                    RecordDeployedRelease(stackId, release.Id);
                // (Re)wire this stack's routes into the reverse proxy: recreated service containers must
                // rejoin the edge network and Caddy must reload. No-op when the proxy is disabled;
                // best-effort so a proxy hiccup never fails an otherwise successful deploy.
                try {
                    await _proxy.ConnectStackAsync(stackId, ct);
                    await _proxy.ApplyAsync(ct);
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
            // Released first: the next stack waiting on the gate should not sit behind this one's
            // temp-file cleanup.
            if (gateHeld) _deployGate.Release();
            _broadcaster.Complete(eventId);
            SafeDelete(tempRepoDir);
            if (dockerConfigDir is not null) SafeDelete(dockerConfigDir);
            if (envFilePath is not null) SafeDeleteFile(envFilePath);
            if (overrideFilePath is not null) SafeDeleteFile(overrideFilePath);
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

    /// <summary>
    /// The stack with everything the deploy needs to resolve its source: the product carries the
    /// repository, compose path and credential, the template its branch override (ADR-0026).
    /// </summary>
    private Stack? GetStack(int stackId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.Stacks.AsNoTracking()
            .Include(s => s.Product)
            .Include(s => s.Template)
            .FirstOrDefault(s => s.Id == stackId);
    }

    private string? GetCredentialToken(int credentialId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.Credentials.AsNoTracking()
            .Where(c => c.Id == credentialId).Select(c => c.Token).FirstOrDefault();
    }

    /// <summary>
    /// The reserved variables injected into every deploy, in write order. <c>WATCHTOWER_URL</c> is
    /// only written when a public base URL is configured.
    /// </summary>
    /// <param name="stackId">The stack being deployed.</param>
    /// <param name="appApiToken">The stack's App API bearer token.</param>
    /// <param name="publicBaseUrl">
    /// Configured <c>Watchtower:PublicBaseUrl</c>. Passed in rather than read here so the env file and
    /// the compose override of one deploy cannot disagree about it.
    /// </param>
    private static List<(string Key, string Value)> BuildReservedEnvVars(
        int stackId, string appApiToken, string? publicBaseUrl, string? authJwksUrl) {
        var vars = new List<(string Key, string Value)> {
            (AppApiTokens.TokenVariable, appApiToken),
            (AppApiTokens.StackIdVariable, stackId.ToString(CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
            vars.Add((AppApiTokens.BaseUrlVariable, publicBaseUrl.Trim()));
        if (!string.IsNullOrWhiteSpace(authJwksUrl))
            vars.Add((AppApiTokens.JwksUrlVariable, authJwksUrl.Trim()));
        return vars;
    }

    /// <summary>
    /// The service a tenant's template routes to — the default recipient of the App API token when no
    /// service opts in by label. Null for a standalone stack, or when the template has gone.
    /// </summary>
    private string? GetTemplateTargetService(int? templateId) {
        if (templateId is not { } id) return null;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.StackTemplates.AsNoTracking()
            .Where(t => t.Id == id).Select(t => t.TargetServiceName).FirstOrDefault();
    }

    /// <summary>Quotes a value for the compose <c>.env</c> file when it embeds a newline or a quote.</summary>
    private static string EscapeEnvValue(string value) =>
        value.Contains('\n') || value.Contains('"')
            ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : value;

    /// <summary>
    /// Composes the temp env-file body from three layers: Watchtower's reserved variables, the
    /// operator's <see cref="StackEnvVar"/>s, and whatever remains of the repository's own
    /// <c>.env</c> — in that physical order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Precedence — repository lowest, then operator, then reserved — is applied by <em>omission</em>
    /// rather than by relying on last-wins duplicate-key semantics inside a single file (which is not
    /// a documented Compose guarantee): a repository key that the operator or Watchtower also defines
    /// is simply not written, and an operator key using a reserved name is skipped. The three sets of
    /// keys written are therefore pairwise disjoint, so <em>file order carries no precedence
    /// meaning</em> and is free to be chosen for safety instead.
    /// </para>
    /// <para>
    /// It is chosen so that the repository block comes <em>last</em>. Repository lines are copied
    /// through verbatim to preserve their original quoting and escaping, which means Watchtower is not
    /// the authority on where a repository's quoted value ends — dotenv's escaping rules have corners
    /// (<c>KEY="C:\Users\"</c>) that a line-oriented reader can misjudge. Emitting Watchtower-owned and
    /// operator-owned lines first makes it structurally impossible for a repository value to swallow
    /// them, whatever the parser concludes: a mis-parsed repository block can at worst corrupt the rest
    /// of the repository's own variables. The reserved <c>WATCHTOWER_*</c> lines are always intact.
    /// </para>
    /// </remarks>
    /// <param name="repoEntries">Entries parsed from the repository's <c>.env</c>, in file order.</param>
    /// <param name="operatorVars">The stack's operator-defined variables.</param>
    /// <param name="reservedVars">Watchtower's reserved variables, highest precedence.</param>
    /// <returns>The file body plus the number of operator and repository variables written.</returns>
    private static (string Content, int OperatorCount, int RepoCount) BuildEnvFileContent(
        IReadOnlyList<(string Key, string RawText)> repoEntries,
        IReadOnlyList<(string Key, string Value)> operatorVars,
        IReadOnlyList<(string Key, string Value)> reservedVars) {
        // Keys a higher-precedence layer defines; a repository entry for any of these is dropped
        // rather than overwritten, which is what frees the physical ordering below.
        var overridden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in operatorVars)
            if (!AppApiTokens.Reserved.Contains(v.Key)) overridden.Add(v.Key);
        foreach (var v in reservedVars) overridden.Add(v.Key);

        var content = new StringBuilder();

        // 1. Reserved, first — nothing the repository writes can reach back over these.
        foreach (var (key, value) in reservedVars)
            content.AppendLine($"{key}={EscapeEnvValue(value)}");

        // 2. Operator variables. Values are re-encoded here, so their quoting is Watchtower's own.
        var operatorCount = 0;
        foreach (var v in operatorVars) {
            // An operator variable using a reserved name is skipped so the injected value wins.
            if (AppApiTokens.Reserved.Contains(v.Key)) continue;
            content.AppendLine($"{v.Key}={EscapeEnvValue(v.Value)}");
            operatorCount++;
        }

        // 3. The repository block last, verbatim and in file order (so duplicate keys inside the
        //    repository's own file still resolve the way Compose would have resolved them).
        var repoCount = 0;
        foreach (var (key, rawText) in repoEntries) {
            if (overridden.Contains(key)) continue;
            content.AppendLine(rawText);
            repoCount++;
        }

        return (content.ToString(), operatorCount, repoCount);
    }

    /// <summary>
    /// Reads the repository's own <c>.env</c> — the file Compose would have auto-loaded from the
    /// project directory (the compose file's directory) had <c>--env-file</c> not been passed.
    /// Returns an empty list when the repository ships no <c>.env</c> or it cannot be read.
    /// </summary>
    /// <param name="composeFilePath">Absolute path to the cloned compose file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed entries, in file order.</returns>
    private async Task<RepoEnv> ReadRepoEnvEntriesAsync(string composeFilePath, CancellationToken ct) {
        var projectDir = Path.GetDirectoryName(composeFilePath);
        if (string.IsNullOrEmpty(projectDir)) return RepoEnv.Empty;
        var repoEnvPath = Path.Combine(projectDir, ".env");
        if (!File.Exists(repoEnvPath)) return RepoEnv.Empty;
        try {
            return ParseEnvEntries(await File.ReadAllLinesAsync(repoEnvPath, ct));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // An unreadable .env must never fail the deploy — degrade to "no repository env".
            _logger.LogWarning(ex, "Could not read repository .env at {Path}; continuing without it", repoEnvPath);
            return RepoEnv.Empty;
        }
    }

    /// <summary>Entries parsed from a repository <c>.env</c>, plus the keys that had to be discarded.</summary>
    /// <param name="Entries">Usable entries, in file order.</param>
    /// <param name="DroppedKeys">Keys discarded as malformed; surfaced in the deploy output.</param>
    private sealed record RepoEnv(
        IReadOnlyList<(string Key, string RawText)> Entries, IReadOnlyList<string> DroppedKeys) {
        /// <summary>No repository <c>.env</c>, or one that could not be read.</summary>
        public static readonly RepoEnv Empty = new([], []);
    }

    /// <summary>
    /// Parses simple <c>KEY=VALUE</c> entries, keeping each entry's source text verbatim. Blank lines
    /// and comments are dropped; an <c>export </c> prefix is tolerated; a value opening a quote that
    /// the line never closes continues onto the following lines and is kept with the entry.
    /// </summary>
    /// <remarks>
    /// An entry whose opening quote is never closed before end-of-file is <em>discarded</em>, not
    /// salvaged, and only that entry is lost: parsing resumes at the next line so the assignments
    /// after it are still read. This protects the repository's own remaining variables from one
    /// malformed line.
    /// <para>
    /// It is deliberately <em>not</em> what protects Watchtower's injected variables. This reader is
    /// line-oriented and does not model dotenv's backslash escaping, so it can misjudge where a quoted
    /// value ends (<c>KEY="C:\Users\"</c> looks closed to it and open to Compose). That is tolerable
    /// only because <see cref="BuildEnvFileContent"/> emits the repository block <em>last</em>: there
    /// are no Watchtower-owned or operator-owned lines after it for a runaway value to swallow.
    /// Ordering is the guarantee; this drop is a courtesy to the repository.
    /// </para>
    /// </remarks>
    /// <param name="lines">The env file's lines.</param>
    /// <returns>The usable entries in file order, plus the keys discarded as malformed.</returns>
    private static RepoEnv ParseEnvEntries(string[] lines) {
        var entries = new List<(string, string)>();
        var dropped = new List<string>();
        for (var i = 0; i < lines.Length; i++) {
            if (ParseEnvKey(lines[i]) is not { } key) continue;

            // Single-line value: the common case.
            if (UnterminatedQuote(lines[i]) is not { } quote) {
                entries.Add((key, lines[i]));
                continue;
            }

            // The value opens a quote. Look ahead for the line that closes it WITHOUT consuming
            // anything yet, so that a quote which is never closed costs only its own line.
            var closingLine = -1;
            for (var j = i + 1; j < lines.Length; j++) {
                if (!lines[j].Contains(quote)) continue;
                closingLine = j;
                break;
            }

            if (closingLine < 0) {
                // Malformed: drop just this entry and carry on parsing the remaining lines. Consuming
                // to end-of-file instead would silently swallow every later assignment as part of the
                // broken value — losing valid repository variables on top of the malformed one.
                dropped.Add(key);
                continue;
            }

            var block = new StringBuilder(lines[i]);
            for (var j = i + 1; j <= closingLine; j++)
                block.Append('\n').Append(lines[j]);
            entries.Add((key, block.ToString()));
            i = closingLine;
        }
        return new RepoEnv(entries, dropped);
    }

    /// <summary>Returns the assigned key of a <c>KEY=VALUE</c> line, or null when the line is not one.</summary>
    private static string? ParseEnvKey(string line) {
        var text = line.TrimStart();
        if (text.Length == 0 || text[0] == '#') return null;
        if (text.StartsWith("export ", StringComparison.Ordinal)) text = text[7..].TrimStart();
        var equals = text.IndexOf('=');
        if (equals <= 0) return null;
        var key = text[..equals].TrimEnd();
        // Anything with whitespace inside is not an assignment (e.g. a wrapped value line).
        return key.Length == 0 || key.Any(char.IsWhiteSpace) ? null : key;
    }

    /// <summary>The quote character a value opens but does not close on this line, if any.</summary>
    private static char? UnterminatedQuote(string line) {
        var equals = line.IndexOf('=');
        if (equals < 0) return null;
        var value = line[(equals + 1)..].TrimStart();
        if (value.Length == 0 || (value[0] != '"' && value[0] != '\'')) return null;
        return value.IndexOf(value[0], 1) >= 0 ? null : value[0];
    }

    /// <summary>
    /// Refreshes the CI toolchain profile for the deployed product's repository (no-op unless CI is
    /// enabled for it). Resolved through a scope like <see cref="EnsureAppApiTokenAsync"/> so the
    /// recorder stays out of this singleton's constructor surface.
    /// </summary>
    private async Task<string?> RecordCiToolchainsAsync(int productId, string cloneDir, CancellationToken ct) {
        using var scope = _scopeFactory.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<CiToolchainRecorder>();
        return await recorder.TryRecordAsync(productId, cloneDir, ct);
    }

    /// <summary>
    /// Reads (generating and persisting on first use) the stack's App API token, so the value written
    /// into the deploy environment is the same one <c>/api/app/*</c> will authenticate against.
    /// </summary>
    private async Task<string> EnsureAppApiTokenAsync(int stackId, CancellationToken ct) {
        using var scope = _scopeFactory.CreateScope();
        var appApi = scope.ServiceProvider.GetRequiredService<AppApiService>();
        return await appApi.EnsureTokenAsync(stackId, ct);
    }

    private List<StackDeviceMapping> GetDeviceMappings(int stackId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.StackDeviceMappings.AsNoTracking()
            .Where(m => m.StackId == stackId)
            .ToList();
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

    /// <summary>
    /// The release this deploy is of — <c>PinnedReleaseId ?? newest</c> (<see cref="ReleaseResolver"/>).
    /// </summary>
    /// <remarks>
    /// Only called once <see cref="ReleaseResolver.UsesReleases"/> has said there is something to
    /// resolve, so a <c>Git</c>-mode deploy opens no scope and touches no <c>DbContext</c> for an answer
    /// that is null by construction. The resolver re-checks the same predicate — it is the contract, and
    /// this is the fast path in front of it.
    /// </remarks>
    private async Task<Release?> ResolveReleaseAsync(Stack stack, CancellationToken ct) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await ReleaseResolver.ResolveAsync(stack, db, _logger, ct);
    }

    /// <summary>Stamps the resolved release onto the tracking event.</summary>
    private void RecordEventRelease(int eventId, int releaseId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.DeployEvents.Where(e => e.Id == eventId)
            .ExecuteUpdate(s => s.SetProperty(e => e.ReleaseId, releaseId));
    }

    /// <summary>Records the release a successful deploy left the stack on.</summary>
    private void RecordDeployedRelease(int stackId, int releaseId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdate(s => s.SetProperty(x => x.LastDeployedReleaseId, releaseId));
    }

    private void RecordDeployedCommit(int stackId, string commitSha) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdate(s => s.SetProperty(x => x.LastDeployedCommit, commitSha));
    }

    private void DeleteUpdateCheck(int stackId) {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.StackUpdateChecks.Where(c => c.StackId == stackId).ExecuteDelete();
    }

    private async Task<string> CreateRegistryConfigDirAsync(CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var builder = scope.ServiceProvider.GetRequiredService<RegistryAuthBuilder>();
        return await builder.CreateTempConfigDirAsync(ct);
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
