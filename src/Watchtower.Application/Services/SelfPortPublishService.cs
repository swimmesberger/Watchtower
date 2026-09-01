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
    private const string KeyRuntime = "proxy.ports.runtime";

    /// <summary><inheritdoc cref="SelfUpdateService.StartupReconcileTimeout" path="/summary"/></summary>
    internal static readonly TimeSpan StartupReconcileTimeout = TimeSpan.FromSeconds(60);

    /// <summary><inheritdoc cref="SelfUpdateService.ApplyWatchTimeout" path="/summary"/></summary>
    internal static readonly TimeSpan ApplyWatchTimeout = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DockerEngineClient _docker;
    private readonly SelfUpdateService _self;
    private readonly IRoleLease _issuerLease;
    private readonly WatchtowerOptions _options;
    private readonly ILogger<SelfPortPublishService> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _applyLock = new();
    private readonly TimeSpan _startupReconcileTimeout;
    private readonly TimeSpan _applyWatchTimeout;
    private Task? _applyTask;

    public SelfPortPublishService(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        SelfUpdateService self,
        [FromKeyedServices(CertificateManager.IssuerRole)] IRoleLease issuerLease,
        IOptions<WatchtowerOptions> options,
        ILogger<SelfPortPublishService> logger)
        : this(scopeFactory, docker, self, issuerLease, options, logger,
            StartupReconcileTimeout, ApplyWatchTimeout) { }

    /// <summary>Test seam: the two ceilings are injectable so a test need not wait out the real ones.</summary>
    internal SelfPortPublishService(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        SelfUpdateService self,
        IRoleLease issuerLease,
        IOptions<WatchtowerOptions> options,
        ILogger<SelfPortPublishService> logger,
        TimeSpan startupReconcileTimeout,
        TimeSpan applyWatchTimeout) {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _self = self;
        _issuerLease = issuerLease;
        _options = options.Value;
        _logger = logger;
        _startupReconcileTimeout = startupReconcileTimeout;
        _applyWatchTimeout = applyWatchTimeout;
    }

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

            await ReconcileManagedPortsAsync(linked.Token);
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
    /// </remarks>
    internal static IReadOnlySet<int> BoundHostPorts(JsonObject inspect) {
        ArgumentNullException.ThrowIfNull(inspect);
        var ports = new HashSet<int>();
        if (inspect["HostConfig"]?["PortBindings"] is not JsonObject bindings) return ports;

        foreach (var (_, value) in bindings) {
            if (value is not JsonArray entries) continue;
            foreach (var entry in entries) {
                if (HostPortText(entry?["HostPort"]) is not { } text) continue;
                foreach (var port in PortRouteListeners.Parse(text)) ports.Add(port);
            }
        }
        return ports;
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
                Ports: [.. routes.Select(r => new HostPortBinding(r.Port, r.RouteId, r.ServiceName, false, false))],
                PendingUnpublish: []);
        }

        var bound = BoundHostPorts(inspect.Value.Inspect);
        var managed = await LoadManagedPortsAsync(ct);
        // The releases come off the plan rather than being recomputed here, so the banner can never
        // offer something the apply would not do.
        var plan = ComputePlan([.. routes.Select(r => r.Port)], bound, managed);
        return new SelfPortPublishStatus(
            ContainerDetected: true,
            UnavailableReason: MultiInstanceRefusal(),
            LastError: runtime.ApplyError,
            Ports: [.. routes.Select(r => new HostPortBinding(
                r.Port, r.RouteId, r.ServiceName, bound.Contains(r.Port), managed.Contains(r.Port)))],
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

        var inspect = await TryInspectSelfRawAsync(ct)
            ?? throw new InvalidOperationException(NotContainerised);

        var desired = (await DesiredRoutesAsync(ct)).Select(r => r.Port).ToList();
        var plan = ComputePlan(desired, BoundHostPorts(inspect.Inspect), await LoadManagedPortsAsync(ct));
        if (plan.IsNoOp) return plan;

        // Written before the spawn, because the coordinator ends this process — see the setting's own
        // documentation for why claiming a port the recreate might not reach is the safe direction.
        await SaveManagedPortsAsync(plan.NextManaged, ct);

        lock (_applyLock) {
            if (_applyTask is not null && !_applyTask.IsCompleted)
                throw new InvalidOperationException(
                    "A host-port change is already being applied. Wait for Watchtower to restart.");
            _applyTask = SpawnAndWatchAsync(inspect.ContainerId, inspect.ImageName, plan, actor, _cts.Token);
        }

        // The start is the only success this process can record: on a successful apply the coordinator
        // replaces this container before there is an outcome to write.
        await RecordAuditAsync("ports.apply", AuditTarget, Describe(plan), actor: actor);
        return plan;
    }

    private async Task SpawnAndWatchAsync(
        string containerId, string imageName, PortBindingPlan plan, string? actor, CancellationToken ct) {
        try {
            await SetStageAsync("restarting", error: null, ct);

            var name = $"watchtower-port-coordinator-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            // The container's current image, not a pulled one: nothing is being updated, so the
            // coordinator runs exactly the code this instance is running.
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
