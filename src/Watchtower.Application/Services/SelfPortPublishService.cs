using System.Globalization;
using System.Text.Json.Nodes;
using Elarion.Abstractions.Coordination;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Services;

/// <summary>
/// Publishes the port routes' listen ports on Watchtower's own container (ADR-0033).
/// </summary>
/// <remarks>
/// A port route needs two things to be reachable: a TLS listener inside the container, which the route
/// table and the Kestrel projection already give it, and the same port published on the container, which
/// Docker cannot add to a container that is running. Watchtower already recreates its own container for
/// the self-update, so this reuses that: the same coordinator binary, spawned with a port amendment
/// instead of a new image, recreating this container in a confirmed restart of a few seconds.
/// <para>
/// The shape is <see cref="SelfUpdateService"/>'s — one apply mutex held by the task that watches the
/// coordinator, a runtime record for the stage, a startup reconcile of whatever the previous process
/// instance left behind — and the mechanics of launching and waiting for the coordinator are literally
/// shared (<see cref="CoordinatorContainers"/>). What is <em>not</em> shared is the runtime record.
/// <c>self.runtime</c> is a self-update's state, down to a stage named "pulling" and an error about an
/// image; a port publish writing into it would mislabel itself and stamp on a self-update genuinely in
/// flight. Two records, one mechanism.
/// </para>
/// <para>
/// Only ports Watchtower itself published are ever taken away again — that is what
/// <see cref="WatchtowerSettingPaths.ProxyYarpManagedHostPorts"/> is for, and why the plan never
/// contains an unpublish for a port the operator declared.
/// </para>
/// </remarks>
public sealed class SelfPortPublishService : IHostedService, IDisposable {
    /// <summary>
    /// Where the apply state lives. Internal because the self-update path has to read it before spawning
    /// a coordinator of its own — see <see cref="CoordinatorContainers.OtherRecreateInFlightAsync"/>.
    /// </summary>
    internal const string KeyRuntime = "proxy.ports.runtime";

    /// <summary><inheritdoc cref="SelfUpdateService.StartupReconcileTimeout" path="/summary"/></summary>
    internal static readonly TimeSpan StartupReconcileTimeout = TimeSpan.FromSeconds(60);

    /// <summary><inheritdoc cref="SelfUpdateService.ApplyWatchTimeout" path="/summary"/></summary>
    internal static readonly TimeSpan ApplyWatchTimeout = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DockerEngineClient _docker;
    private readonly SelfUpdateService _self;
    private readonly HostPortOccupancy _hostPorts;
    private readonly IRoleLease _issuerLease;
    private readonly WatchtowerOptions _options;
    private readonly ILogger<SelfPortPublishService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _applyLock = new();
    private readonly TimeSpan _startupReconcileTimeout;
    private readonly TimeSpan _applyWatchTimeout;
    private Task? _applyTask;

    /// <summary>
    /// Held from passing the apply mutex until <see cref="_applyTask"/> exists, so the settings write
    /// that happens in between is inside the accepted branch rather than in front of it. Without it a
    /// second, rejected call would still have claimed the ports on its way to being refused. Read and
    /// written only under <see cref="_applyLock"/>.
    /// </summary>
    private bool _applyReserved;

    public SelfPortPublishService(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        SelfUpdateService self,
        HostPortOccupancy hostPorts,
        [FromKeyedServices(CertificateManager.IssuerRole)] IRoleLease issuerLease,
        IOptions<WatchtowerOptions> options,
        ILogger<SelfPortPublishService> logger)
        : this(scopeFactory, docker, self, hostPorts, issuerLease, options, logger,
            StartupReconcileTimeout, ApplyWatchTimeout) { }

    /// <summary>
    /// Test seam: the two ceilings are injectable so a test need not wait out the real ones, and the two
    /// callbacks hold the apply at the two instants nothing else can observe.
    /// </summary>
    /// <param name="beforeSpawn">
    /// Awaited before the spawn task does anything at all. It exists for one assertion that cannot be
    /// made without it: that everything the <em>other</em> recreate path's guard reads has been published
    /// by the time <see cref="ApplyAsync"/> returns. Held only by that task, so nothing a caller observes
    /// afterwards can have come from it — which is the whole point, since a background task that merely
    /// tends to be slower would make the same assertion pass while the ordering was wrong.
    /// </param>
    /// <param name="beforeVerify">
    /// Awaited between this path claiming its own stage and re-reading the other path's. It is the only
    /// way to stand inside the window the claim-then-verify guard closes: in production the other path
    /// writes its stage there of its own accord, and a test cannot make two applies interleave on demand.
    /// </param>
    internal SelfPortPublishService(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        SelfUpdateService self,
        HostPortOccupancy hostPorts,
        IRoleLease issuerLease,
        IOptions<WatchtowerOptions> options,
        ILogger<SelfPortPublishService> logger,
        TimeSpan startupReconcileTimeout,
        TimeSpan applyWatchTimeout,
        Func<CancellationToken, Task>? beforeSpawn = null,
        Func<CancellationToken, Task>? beforeVerify = null) {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _self = self;
        _hostPorts = hostPorts;
        _issuerLease = issuerLease;
        _options = options.Value;
        _logger = logger;
        _startupReconcileTimeout = startupReconcileTimeout;
        _applyWatchTimeout = applyWatchTimeout;
        _beforeSpawn = beforeSpawn;
        _beforeVerify = beforeVerify;
    }

    /// <inheritdoc cref="SelfPortPublishService(IServiceScopeFactory, DockerEngineClient, SelfUpdateService, HostPortOccupancy, IRoleLease, IOptions{WatchtowerOptions}, ILogger{SelfPortPublishService}, TimeSpan, TimeSpan, Func{CancellationToken, Task}, Func{CancellationToken, Task})" path="/param[@name='beforeSpawn']"/>
    private readonly Func<CancellationToken, Task>? _beforeSpawn;

    /// <inheritdoc cref="SelfPortPublishService(IServiceScopeFactory, DockerEngineClient, SelfUpdateService, HostPortOccupancy, IRoleLease, IOptions{WatchtowerOptions}, ILogger{SelfPortPublishService}, TimeSpan, TimeSpan, Func{CancellationToken, Task}, Func{CancellationToken, Task})" path="/param[@name='beforeVerify']"/>
    private readonly Func<CancellationToken, Task>? _beforeVerify;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a coordinator the previous process instance never saw finish, then reconciles the
    /// managed set against what the container actually publishes.
    /// </summary>
    /// <remarks>
    /// The reconcile is what makes the pre-spawn write of the managed set safe (see
    /// <see cref="WatchtowerSettingPaths.ProxyYarpManagedHostPorts"/>). Two things produce a claim on a
    /// port that is not bound, and both are ordinary: a recreate that failed and rolled back, and an
    /// operator running <c>docker compose up -d</c>, which rebuilds the container from the compose file
    /// and drops whatever this service added. Pruning the claim in both cases is what puts the port back
    /// on the Routes page as "not published" — offering to publish it again, rather than believing it is
    /// already done.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        try {
            var runtime = await LoadRuntimeAsync(linked.Token);
            if (runtime is { ApplyStage: "restarting", CoordinatorId: { } coordinatorId })
                await ReconcileCoordinatorAsync(coordinatorId, _startupReconcileTimeout, linked.Token);
            else if (runtime.ApplyStage == "restarting")
                await SetStageAsync("idle", error: null, linked.Token);

            // Order matters: the prune below is what turns a release that really landed into an empty
            // plan, which is what the clear after it reads.
            await ReconcileManagedPortsAsync(linked.Token);
            await ClearResolvedApplyErrorAsync(linked.Token);
        } catch (OperationCanceledException) {
            // Shutting down before the reconcile finished; the next start picks it up.
        } catch (Exception ex) {
            // Never fatal to startup: this is bookkeeping about ports, and the instance serves without it.
            _logger.LogWarning(ex, "Reconciling the published host ports failed; it will be retried on the next start.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        await _cts.CancelAsync();
        Task? running;
        lock (_applyLock) { running = _applyTask; }
        if (running is not null)
            await Task.WhenAny(running, Task.Delay(Timeout.Infinite, cancellationToken));
    }

    public void Dispose() => _cts.Dispose();

    // ── The plan ──────────────────────────────────────────────────────────────

    /// <summary>
    /// What an apply would do, from the three sets it depends on and nothing else — pure, so the rule
    /// that decides whether an operator's binding can be taken away is testable without a daemon.
    /// </summary>
    /// <param name="desired">The listen ports of the port routes.</param>
    /// <param name="bound">The host ports the container currently publishes.</param>
    /// <param name="managed">The host ports Watchtower published itself.</param>
    /// <remarks>
    /// Three rules, and the middle one is the safety property:
    /// <list type="bullet">
    ///   <item>publish what is wanted and not bound — a port the operator already publishes satisfies its
    ///     route as it is, so it is not republished and, importantly, not adopted either;</item>
    ///   <item>unpublish only what is <em>both</em> claimed and bound and no longer wanted — so a binding
    ///     Watchtower never made can never be removed, and a stale claim can remove nothing;</item>
    ///   <item>the next managed set keeps the claims that are still true and adds the ports about to be
    ///     published.</item>
    /// </list>
    /// <see cref="PortBindingPlan.NextManaged"/> is the set that holds <em>once the recreate has
    /// happened</em>; what is written before it is <see cref="PortBindingPlan.ClaimedThroughTheRecreate"/>,
    /// which keeps the released ports until the release actually lands.
    /// </remarks>
    public static PortBindingPlan ComputePlan(
        IReadOnlyCollection<int> desired, IReadOnlyCollection<int> bound, IReadOnlyCollection<int> managed) {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(managed);

        var wanted = new HashSet<int>(desired);
        var isBound = new HashSet<int>(bound);
        var isManaged = new HashSet<int>(managed);

        var publish = Sorted(wanted.Where(p => !isBound.Contains(p)));
        var unpublish = Sorted(isManaged.Where(p => isBound.Contains(p) && !wanted.Contains(p)));
        var nextManaged = Sorted(
            isManaged.Where(p => isBound.Contains(p) && wanted.Contains(p)).Concat(publish));

        return new PortBindingPlan(publish, unpublish, nextManaged);
    }

    private static List<int> Sorted(IEnumerable<int> ports) => [.. new SortedSet<int>(ports)];

    /// <summary>
    /// The host ports a container inspect record declares. Read off <c>HostConfig.PortBindings</c> — the
    /// declared configuration, which is what a clone carries forward — rather than off
    /// <c>NetworkSettings.Ports</c>, which also reports what the daemon assigned at run time.
    /// </summary>
    /// <remarks>
    /// Tolerant in the same way <see cref="PortRouteListeners.Parse"/> is, and for the same reason: an
    /// entry it cannot read costs that entry and nothing else. An empty <c>HostPort</c> — Docker's "give
    /// me any free port" — is not a port anything can be addressed at, so it is dropped too.
    /// <para>
    /// Only the TCP half of the map counts. A binding's key carries the protocol
    /// (<c>9001/tcp</c>, <c>9001/udp</c>), and a port route serves HTTPS — so a container publishing
    /// <c>9001/udp</c> publishes nothing this feature can use. Reading it as satisfied would leave the
    /// route permanently unreachable with the page reporting the port as published, and no apply would
    /// ever add the TCP binding. The same rule <see cref="ContainerCloneSpec"/> writes with.
    /// </para>
    /// </remarks>
    internal static IReadOnlySet<int> BoundHostPorts(JsonObject inspect) {
        ArgumentNullException.ThrowIfNull(inspect);
        var ports = new HashSet<int>();
        if (inspect["HostConfig"]?["PortBindings"] is not JsonObject bindings) return ports;

        foreach (var (key, value) in bindings) {
            if (!IsTcp(key)) continue;
            if (value is not JsonArray entries) continue;
            foreach (var entry in entries) {
                // Pattern-matched to an object rather than indexed straight through: the string indexer
                // throws on a node that is not one, and this reading is bookkeeping about ports — an
                // entry Docker would never have written costs that entry, not the whole inspect.
                if (entry is not JsonObject binding) continue;
                if (HostPortText(binding["HostPort"]) is not { } text) continue;
                foreach (var port in PortRouteListeners.Parse(text)) ports.Add(port);
            }
        }
        return ports;
    }

    /// <summary>
    /// Whether a <c>PortBindings</c> key names a TCP port. A key with no suffix at all is tcp, which is
    /// what the daemon assumes for a bare port number too.
    /// </summary>
    private static bool IsTcp(string key) {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 || key.AsSpan(slash + 1).Equals("tcp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Docker writes the host port as a string; a number is accepted rather than refused.</summary>
    private static string? HostPortText(JsonNode? node) => node switch {
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonValue v when v.TryGetValue<int>(out var number) => number.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>
    /// How the port routes' ports stand on this container, for the Routes page: per row whether the host
    /// port is published, and whether an apply is possible at all.
    /// </summary>
    /// <remarks>
    /// Never throws. An undetectable container is a state the page renders (with the manual instructions
    /// instead of a button), not an error it shows in place of the route list.
    /// </remarks>
    public async Task<SelfPortPublishStatus> GetStatusAsync(CancellationToken ct = default) {
        var runtime = await LoadRuntimeAsync(ct);
        var routes = await DesiredRoutesAsync(ct);
        var inspect = await TryInspectSelfRawAsync(ct);

        if (inspect is null) {
            return new SelfPortPublishStatus(
                ContainerDetected: false,
                UnavailableReason: NotContainerised,
                LastError: runtime.ApplyError,
                Ports: [.. routes.Select(r => new HostPortBinding(
                    r.Port, r.RouteId, r.ServiceName, false, false, BlockedBy: null))],
                PendingUnpublish: []);
        }

        var bound = BoundHostPorts(inspect.Value.Inspect);
        var managed = await LoadManagedPortsAsync(ct);
        // The releases come off the plan rather than being recomputed here, so the banner can never
        // offer something the apply would not do.
        var plan = ComputePlan([.. routes.Select(r => r.Port)], bound, managed);
        // Only the ports an apply would try to publish are worth asking Docker about: a port already
        // bound here is not blocked by definition. Once a deployment is converged the plan is empty and
        // this costs no call; while a port is pending it is one GET /containers/json?all=true per status
        // poll, which the page makes every 15 s and only for as long as the port is unpublished.
        var blocked = await _hostPorts.PublishedByOtherContainersAsync(plan.Publish, inspect.Value.ContainerId, ct);
        return new SelfPortPublishStatus(
            ContainerDetected: true,
            UnavailableReason: MultiInstanceRefusal(),
            LastError: runtime.ApplyError,
            Ports: [.. routes.Select(r => new HostPortBinding(
                r.Port, r.RouteId, r.ServiceName, bound.Contains(r.Port), managed.Contains(r.Port),
                blocked.TryGetValue(r.Port, out var holder) ? holder : null))],
            PendingUnpublish: plan.Unpublish);
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes and unpublishes the host ports the port routes call for, by recreating this container.
    /// Returns as soon as the coordinator is running — it waits three seconds before stopping this
    /// container, which is what lets the caller answer the request that asked for this.
    /// </summary>
    /// <returns>The plan that was carried out; <see cref="PortBindingPlan.IsNoOp"/> when nothing was done.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the apply is refused: not running as a container, another instance is known to exist,
    /// or an apply is already in flight. The message is operator-facing.
    /// </exception>
    public async Task<PortBindingPlan> ApplyAsync(string? actor = null, CancellationToken ct = default) {
        if (MultiInstanceRefusal() is { } refusal) throw new InvalidOperationException(refusal);

        // The self-update recreates the same container from the same coordinator binary. Its mutex is
        // not this one, so without this check the two spawn coordinators that race over one container id.
        // This reading is the cheap refusal, not the guard: several round trips separate it from the
        // spawn, and the guard that actually holds is the claim-then-verify below.
        if (await CoordinatorContainers.OtherRecreateInFlightAsync(
                _scopeFactory, CoordinatorContainers.CoordinatorKind.PortPublish, ct) is { } busy)
            throw new InvalidOperationException(busy);

        var inspect = await TryInspectSelfRawAsync(ct)
            ?? throw new InvalidOperationException(NotContainerised);

        var desired = (await DesiredRoutesAsync(ct)).Select(r => r.Port).ToList();
        var plan = ComputePlan(desired, BoundHostPorts(inspect.Inspect), await LoadManagedPortsAsync(ct));
        if (plan.IsNoOp) {
            // The container is already in the state the button asks for, so an error a previous apply
            // recorded is about a world that no longer exists — usually an operator who added the -p by
            // hand afterwards. Cleared here because nothing else would: this branch returns before the
            // stage write, and once every port is bound the page stops offering the apply at all.
            await ClearApplyErrorAsync(ct);
            return plan;
        }

        // Refused here rather than discovered by the daemon. Recreating this container with a port
        // another container already publishes fails at start, rolls back, and the operator is left with
        // "host port not published" and a rolled-back restart — where what they need is the name of the
        // container holding the port. Fail-open: an unreachable daemon refuses nothing.
        var blocked = await _hostPorts.PublishedByOtherContainersAsync(plan.Publish, inspect.ContainerId, ct);
        if (blocked.Count > 0)
            throw new InvalidOperationException(blocked.OrderBy(e => e.Key).First().Value);

        lock (_applyLock) {
            if (_applyReserved || (_applyTask is not null && !_applyTask.IsCompleted))
                throw new InvalidOperationException(
                    "A host-port change is already being applied. Wait for Watchtower to restart.");
            _applyReserved = true;
        }

        try {
            // Read before the claim overwrites it. A lost race has to put this record back the way it
            // found it — writing a flat "idle" would quietly resolve an error a previous apply recorded,
            // and the operator would lose the only account of why their ports are not published.
            var priorRuntime = await LoadRuntimeAsync(ct);

            // Claim, then verify. The stage goes first — published before the task exists, because it is
            // what the self-update's guard reads, and writing it from inside the task (after that task's
            // first await) would leave both paths able to pass their guard and spawn a coordinator each.
            await SetStageAsync("restarting", error: null, ct);

            // Only for a test, which is the only way to stand inside the window below. Null in production.
            if (_beforeVerify is not null) await _beforeVerify(ct);

            // …and only now is the other record read again. Between the cheap refusal above and this
            // point there is a Docker inspect and several database round trips, and the self-update reads
            // *this* record somewhere in there; without the second look both would pass and both would
            // spawn. Claiming first is what makes the second look conclusive: whichever path writes its
            // stage last sees the other's and stands down. Both standing down in a true tie is the
            // correct outcome — nothing was started, and the operator presses the button again.
            if (await CoordinatorContainers.OtherRecreateInFlightAsync(
                    _scopeFactory, CoordinatorContainers.CoordinatorKind.PortPublish, ct) is { } racing) {
                // Back to exactly what was there before the claim. Not an error of its own — nothing
                // failed — and not a blank "idle" either, which would erase a previous apply's recorded
                // failure; a stage left at "restarting" would block the path that won for as long as this
                // process lives.
                await SetStageAsync(priorRuntime.ApplyStage, priorRuntime.ApplyError, ct);
                throw new InvalidOperationException(racing);
            }

            // After the verify, so a refusal cannot leave a claim behind. Still before the spawn, because
            // the coordinator ends this process and there is no "after". The ports about to be released
            // stay in the claim rather than being dropped in advance: a recreate that rolls back leaves
            // them bound, and the startup prune only ever removes claims, so dropping them here would
            // strand a bound port with nothing able to adopt it. See
            // PortBindingPlan.ClaimedThroughTheRecreate.
            await SaveManagedPortsAsync(plan.ClaimedThroughTheRecreate, ct);
            lock (_applyLock) {
                _applyTask = SpawnAndWatchAsync(inspect.ContainerId, inspect.ImageName, plan, actor, _cts.Token);
            }
        } finally {
            // Released once the task itself is the mutex — or once the attempt has failed without one.
            lock (_applyLock) { _applyReserved = false; }
        }

        // The start is the only success this process can record: on a successful apply the coordinator
        // replaces this container before there is an outcome to write.
        await RecordAuditAsync("ports.apply", AuditTarget, Describe(plan), actor: actor);
        return plan;
    }

    private async Task SpawnAndWatchAsync(
        string containerId, string imageName, PortBindingPlan plan, string? actor, CancellationToken ct) {
        try {
            // First, and deliberately ahead of every other statement — see the seam's own documentation.
            // Null outside tests.
            if (_beforeSpawn is not null) await _beforeSpawn(ct);

            // The "restarting" stage is already published — ApplyAsync writes it before this task
            // exists, so the self-update's guard cannot read a stale "idle".
            var name = $"watchtower-port-coordinator-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            // The container's configured image reference rather than a pulled one: nothing is being
            // updated here. It is the tag, not the resolved id, and deliberately — writing a sha256 into
            // the clone's Config.Image would break the self-update's digest comparison afterwards. The
            // cost is that a tag which moved locally since this process started brings the newer image
            // along; the recreate is faithful either way, and ADR-0033 records the drift.
            var coordinatorId = await CoordinatorContainers.SpawnAsync(
                _docker, imageName,
                [
                    "--self-update",
                    "--container-id", containerId,
                    "--image", imageName,
                    "--publish-ports", PortRouteListeners.Format(plan.Publish),
                    "--unpublish-ports", PortRouteListeners.Format(plan.Unpublish),
                ],
                _options.DockerApiVersion, name, ct);
            await UpdateRuntimeAsync(r => r with { CoordinatorId = coordinatorId }, ct);

            _logger.LogInformation(
                "Coordinator {Name} ({ShortId}) started; it will recreate this container in ~3 s to {Plan}",
                name, CoordinatorContainers.Short(coordinatorId), Describe(plan));

            // During a successful apply this wait is cancelled when the coordinator recreates this
            // container, and the next process instance reconciles at startup. It is watched anyway so a
            // recreate that failed and rolled back surfaces as an error instead of silence.
            await ReconcileCoordinatorAsync(coordinatorId, _applyWatchTimeout, ct);
        } catch (OperationCanceledException) {
            _logger.LogWarning("The host-port apply was cancelled (host shutting down)");
            await SetStageAsync("error", "The host-port change was cancelled — the host was shutting down.",
                CancellationToken.None);
        } catch (Exception ex) {
            _logger.LogError(ex, "Spawning the host-port coordinator failed");
            await SetStageAsync("error", ex.Message, CancellationToken.None);
            await RecordAuditAsync(
                "ports.apply", AuditTarget, Describe(plan), success: false, error: ex.Message, actor: actor);
        }
    }

    /// <summary>
    /// Waits for the coordinator to exit and resolves the outcome: exit 0 clears the stage, anything else
    /// surfaces the coordinator's own logs as the error an operator reads on the Routes page.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="SelfUpdateService.ReconcileCoordinatorAsync"/>, over this service's own
    /// record — see the class remarks for why the record is not shared. The cancellation contract is the
    /// same and matters for the same reason: on the happy path this process dies mid-wait, and leaving
    /// the stage and coordinator id in place is what lets the next process instance finish the job.
    /// </remarks>
    internal async Task ReconcileCoordinatorAsync(string coordinatorId, TimeSpan waitTimeout, CancellationToken ct) {
        try {
            var details = await _docker.InspectContainerAsync(coordinatorId, ct);
            if (details.State?.Status == "running") {
                if (!await CoordinatorContainers.TryWaitForExitAsync(_docker, _logger, coordinatorId, waitTimeout, ct))
                    return;
                details = await _docker.InspectContainerAsync(coordinatorId, ct);
            }

            var exitCode = details.State?.ExitCode ?? -1;
            if (exitCode == 0) {
                _logger.LogInformation(
                    "Coordinator {Id} exited successfully — the host ports were applied",
                    CoordinatorContainers.Short(coordinatorId));
                await SetStageAsync("idle", error: null, ct);
            } else {
                var logs = await CoordinatorContainers.CollectLogsAsync(_docker, coordinatorId, ct);
                _logger.LogError(
                    "Coordinator {Id} exited with code {Code}:\n{Logs}",
                    CoordinatorContainers.Short(coordinatorId), exitCode, logs);
                await SetStageAsync("error",
                    $"Publishing the host ports failed (exit {exitCode}); the previous container was "
                    + $"restored:\n{logs.Trim()}", ct);
            }

            await _docker.RemoveContainerAsync(coordinatorId, ct);
            await UpdateRuntimeAsync(r => r with { CoordinatorId = null }, ct);
        } catch (OperationCanceledException) {
            // Shutting down — most likely because the coordinator just recreated this container. The
            // stage and coordinator id stay for the next process instance to resolve.
        } catch (Exception ex) {
            // Container not found (already removed) most likely means it ran and exited cleanly.
            _logger.LogDebug(ex, "Could not inspect coordinator {Id}; assuming the port change completed",
                CoordinatorContainers.Short(coordinatorId));
            await SetStageAsync("idle", error: null, CancellationToken.None);
            await UpdateRuntimeAsync(r => r with { CoordinatorId = null }, CancellationToken.None);
        }
    }

    // ── Guards ────────────────────────────────────────────────────────────────

    /// <summary>The refusal an operator gets outside Docker, with the manual equivalent of the button.</summary>
    internal const string NotContainerised =
        "Watchtower is not running as a Docker container it can see (or the daemon is unreachable), so it "
        + "cannot republish its own ports. Publish each port on the process yourself — for a container, "
        + "add the matching -p {port}:{port} and recreate it.";

    /// <summary>
    /// Why a multi-instance deployment is refused, or null when no second instance is known.
    /// </summary>
    /// <remarks>
    /// One-directional, and deliberately left that way. The only cheap evidence in the codebase is the
    /// <c>acme-issuer</c> lease (ADR-0024 decision 5): a holder that is not this process proves a second
    /// instance exists. The converse proves nothing — holding the lease, or nobody holding it yet, is
    /// what a single instance looks like <em>and</em> what one node of a cluster looks like. Building a
    /// real instance registry to close that gap is a feature of its own, so what ships is the half that
    /// costs nothing and a documented limitation: on a cluster where this instance happens to hold the
    /// lease, the button is offered and publishes ports on this node's container only, while the managed
    /// set — a Global setting — is shared, so the other nodes' startup reconcile will prune claims their
    /// own containers do not honour. Publish the ports manually on every node instead.
    /// </remarks>
    private string? MultiInstanceRefusal() {
        if (_issuerLease.IsHeld) return null;
        if (string.IsNullOrWhiteSpace(_issuerLease.CurrentHolder)) return null;
        return $"Another Watchtower instance is running (instance {_issuerLease.CurrentHolder}). Publishing "
            + "host ports recreates one container, and each instance has its own — publish the port on "
            + "each node's container by hand instead.";
    }

    // ── Reading the world ─────────────────────────────────────────────────────

    /// <summary>The port routes, in port order — the desired set, and the rows the status reports on.</summary>
    private async Task<IReadOnlyList<(int Port, int RouteId, string ServiceName)>> DesiredRoutesAsync(
        CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var rows = await db.Routes.AsNoTracking()
            .Where(r => r.Binding == RouteBinding.Port && r.ListenPort != null)
            .OrderBy(r => r.ListenPort)
            .Select(r => new { r.Id, r.ListenPort, r.ServiceName })
            .ToListAsync(ct);
        return [.. rows.Select(r => (r.ListenPort!.Value, r.Id, r.ServiceName))];
    }

    /// <summary>
    /// This container's inspect record, plus the two facts the coordinator has to be told. Null when
    /// Watchtower is not running as a container it can see — never an exception, because "cannot see
    /// itself" is a state every caller here has an answer for.
    /// </summary>
    private async Task<(JsonObject Inspect, string ContainerId, string ImageName)?> TryInspectSelfRawAsync(
        CancellationToken ct) {
        // Detected through the same HOSTNAME → inspect the self-update uses, so the two can never
        // disagree about which container Watchtower is.
        var detected = await _self.DetectSelfAsync(ct);
        if (!detected.IsRunningInContainer
            || string.IsNullOrWhiteSpace(detected.ContainerId)
            || string.IsNullOrWhiteSpace(detected.ImageName))
            return null;

        try {
            var inspect = await _docker.InspectContainerRawAsync(detected.ContainerId, ct);
            return (inspect, detected.ContainerId, detected.ImageName);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogDebug(ex, "Could not read the port bindings of container {Id}",
                CoordinatorContainers.Short(detected.ContainerId));
            return null;
        }
    }

    /// <summary>
    /// Drops claims on ports the container does not actually publish. See <see cref="StartAsync"/> for
    /// when that happens and why it is the self-healing half of writing the claim before the recreate.
    /// </summary>
    private async Task ReconcileManagedPortsAsync(CancellationToken ct) {
        var managed = await LoadManagedPortsAsync(ct);
        if (managed.Count == 0) return;

        // A container this process cannot inspect is not evidence that nothing is bound, so nothing is
        // pruned on it — dropping the claims would silently hand every published port to the operator.
        if (await TryInspectSelfRawAsync(ct) is not { } self) return;

        var bound = BoundHostPorts(self.Inspect);
        var kept = Sorted(managed.Where(bound.Contains));
        if (kept.Count == managed.Count) return;

        _logger.LogInformation(
            "Host ports {Dropped} are no longer published on this container; releasing Watchtower's claim on them.",
            string.Join(", ", managed.Where(p => !bound.Contains(p))));
        await SaveManagedPortsAsync(kept, ct);
    }

    /// <summary>
    /// Drops a recorded apply error once an apply would have nothing left to do — whatever the failed one
    /// was going to change has happened by other means. Read before written, so a deployment with nothing
    /// to clear costs one settings read on start and no write at all.
    /// </summary>
    /// <remarks>
    /// The condition is the whole plan being empty, not merely every routed port being published. A
    /// release that failed and rolled back leaves its port bound <em>and</em> still claimed — that is what
    /// <see cref="PortBindingPlan.ClaimedThroughTheRecreate"/> is for — so every routed port can be
    /// published while an unpublish is still outstanding. Clearing on the publishes alone would erase the
    /// error on the very next start after such a failure, while the work it describes is still pending and
    /// the page is still offering the button for it.
    /// </remarks>
    private async Task ClearResolvedApplyErrorAsync(CancellationToken ct) {
        var runtime = await LoadRuntimeAsync(ct);
        if (runtime is { ApplyStage: not "error", ApplyError: null }) return;
        if (await TryInspectSelfRawAsync(ct) is not { } self) return;

        var desired = (await DesiredRoutesAsync(ct)).Select(r => r.Port).ToList();
        var plan = ComputePlan(desired, BoundHostPorts(self.Inspect), await LoadManagedPortsAsync(ct));
        if (!plan.IsNoOp) return;
        await SetStageAsync("idle", error: null, ct);
    }

    /// <summary>Clears a recorded apply error, writing nothing when there is none.</summary>
    private async Task ClearApplyErrorAsync(CancellationToken ct) {
        var runtime = await LoadRuntimeAsync(ct);
        if (runtime is { ApplyStage: not "error", ApplyError: null }) return;
        await SetStageAsync("idle", error: null, ct);
    }

    /// <summary>
    /// What the audit trail calls this change. A constant rather than the port list, so the rows of one
    /// deployment's port history line up under one target the way the settings rows do — the ports
    /// themselves are the detail.
    /// </summary>
    internal const string AuditTarget = "watchtower host ports";

    private static string Describe(PortBindingPlan plan) {
        var parts = new List<string>();
        if (plan.Publish.Count > 0) parts.Add($"publish {string.Join(", ", plan.Publish)}");
        if (plan.Unpublish.Count > 0) parts.Add($"unpublish {string.Join(", ", plan.Unpublish)}");
        return parts.Count == 0 ? "no host-port change" : string.Join(" and ", parts);
    }

    private async Task RecordAuditAsync(
        string action, string target, string? detail, bool success = true, string? error = null, string? actor = null) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuditLog>()
            .RecordAsync("proxy", action, target, detail, success, error, actor);
    }

    // ── Scoped settings access ────────────────────────────────────────────────

    private async Task<IReadOnlyList<int>> LoadManagedPortsAsync(CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        return PortRouteListeners.Parse(
            await settings.GetStringAsync(WatchtowerSettingPaths.ProxyYarpManagedHostPorts, SettingsScope.Global, ct));
    }

    private async Task SaveManagedPortsAsync(IReadOnlyList<int> ports, CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        await settings.SetStringAsync(
            WatchtowerSettingPaths.ProxyYarpManagedHostPorts, PortRouteListeners.Format(ports),
            SettingsScope.Global, expectedVersion: null, ct);
    }

    private async Task<SelfPortPublishRuntime> LoadRuntimeAsync(CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        return await mgr.GetAsync(KeyRuntime, new SelfPortPublishRuntime(), SettingsScope.Global, ct);
    }

    private Task SetStageAsync(string stage, string? error, CancellationToken ct) =>
        UpdateRuntimeAsync(r => r with { ApplyStage = stage, ApplyError = error }, ct);

    /// <summary>Read-modify-write the runtime record (last-write-wins, like the self-update's).</summary>
    private async Task UpdateRuntimeAsync(
        Func<SelfPortPublishRuntime, SelfPortPublishRuntime> mutate, CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        var current = await mgr.GetAsync(KeyRuntime, new SelfPortPublishRuntime(), SettingsScope.Global, ct);
        await mgr.SetAsync(KeyRuntime, mutate(current), SettingsScope.Global, expectedVersion: null, ct);
    }
}
