using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// The in-process reverse proxy — ADR-0022. Watchtower terminates 80/443 itself and
/// forwards to the routed containers, instead of managing a sibling proxy container. It is the third
/// <see cref="IProxyProvider"/> behind <see cref="ProxyProviderRouter"/> and self-gates exactly like
/// the other two — every method no-ops unless the proxy is enabled and <c>yarp</c> is selected.
/// </summary>
/// <remarks>
/// The shape is <see cref="CaddyManager"/>'s, and deliberately so: one transition lock, a diff against
/// the last-applied <see cref="ProxyOptions"/>, and a background reconcile off the startup path. What
/// it does <em>not</em> have is the container half. There is no image to pull, no container to
/// supervise, and no <c>watchtower-control</c> network — Watchtower is the proxy, so the "proxy
/// container" that has to join every stack's ingress network is its own. That also makes
/// <c>Teardown</c> cheap: disabling the provider empties the route table, and the listener the host
/// bound at startup simply stops matching anything.
/// <para>
/// Nothing here binds a socket or terminates TLS yet — this is the control plane. The request path
/// (Kestrel endpoints, the forwarder middleware) and certificate issuance land in later phases; the
/// seams they plug into are <see cref="ProxyRouteTable"/>, <see cref="YarpListenerState"/> and
/// <see cref="IProxyCertificateManager"/>.
/// </para>
/// </remarks>
public class YarpProxyProvider : IHostedService, IProxyProvider, IDisposable {
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxyIngressNetworks _networks;
    private readonly ProxyRouteTable _table;
    private readonly YarpListenerState _listener;
    private readonly IProxyCertificateManager _certs;
    private readonly RouteStatusUpdater _routeStatus;
    private readonly IOptionsMonitor<WatchtowerOptions> _options;
    private readonly ILogger<YarpProxyProvider> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable? _optionsSubscription;
    // Serializes the transitions (startup reconcile, runtime enable/disable, refresh) so a toggle
    // flipped twice in quick succession cannot interleave a teardown with a reconcile.
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private readonly object _appliedGate = new();
    // The proxy settings the provider last acted on; OnChange diffs against this to decide a transition.
    private ProxyOptions _applied;
    private Task? _reconcileTask;

    public YarpProxyProvider(
        IServiceScopeFactory scopeFactory,
        ProxyIngressNetworks networks,
        ProxyRouteTable table,
        YarpListenerState listener,
        IProxyCertificateManager certs,
        RouteStatusUpdater routeStatus,
        IOptionsMonitor<WatchtowerOptions> options,
        ILogger<YarpProxyProvider> logger) {
        _scopeFactory = scopeFactory;
        _networks = networks;
        _table = table;
        _listener = listener;
        _certs = certs;
        _routeStatus = routeStatus;
        _options = options;
        _logger = logger;
        _applied = options.CurrentValue.Proxy;
        _optionsSubscription = options.OnChange(o => OnProxyOptionsChanged(o.Proxy));
    }

    /// <summary>Active only while the proxy is enabled AND the in-process provider is selected.</summary>
    public bool Enabled => IsYarpActive(_options.CurrentValue.Proxy);

    private static bool IsYarpActive(ProxyOptions o) => o.Enabled && o.ResolveProvider() == ProxyProviderKind.Yarp;

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!Enabled) {
            _logger.LogInformation(
                "In-process reverse proxy inactive (disabled or another provider selected); skipping setup. "
                + "It can be enabled at runtime from Settings.");
            return Task.CompletedTask;
        }
        // Reconcile off the startup path: joining every stack's ingress network talks to the Docker
        // daemon, and a slow daemon must not hold host startup up.
        _reconcileTask = Task.Run(() => RunExclusiveAsync(ReconcileAsync, _cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        await _cts.CancelAsync();
        if (_reconcileTask is not null)
            await Task.WhenAny(_reconcileTask, Task.Delay(Timeout.Infinite, cancellationToken));
    }

    public void Dispose() {
        _optionsSubscription?.Dispose();
        _cts.Dispose();
        _transitionLock.Dispose();
    }

    // ── Runtime enable/disable (settings-driven) ──────────────────────────────

    /// <summary>
    /// This provider's view of an options change (<see cref="ProxyTransitions"/>): active means
    /// enabled AND <c>yarp</c> selected, so switching the provider to caddy is a
    /// <see cref="ProxyTransition.Stop"/> here while <see cref="CaddyManager"/> computes a Start from
    /// the same change.
    /// </summary>
    internal static ProxyTransition DecideTransition(ProxyOptions was, ProxyOptions now) =>
        ProxyTransitions.Decide(IsYarpActive(was), IsYarpActive(now), was != now);

    private void OnProxyOptionsChanged(ProxyOptions next) {
        ProxyTransition transition;
        lock (_appliedGate) {
            transition = DecideTransition(_applied, next);
            _applied = next;
        }
        if (transition == ProxyTransition.None) return;
        _logger.LogInformation("Proxy settings changed at runtime ({Transition}).", transition);
        Func<CancellationToken, Task> operation = transition switch {
            ProxyTransition.Start => ReconcileAsync,
            ProxyTransition.Stop => TeardownAsync,
            _ => ApplyAsync,
        };
        _reconcileTask = Task.Run(() => RunExclusiveAsync(operation, _cts.Token), CancellationToken.None);
    }

    private async Task RunExclusiveAsync(Func<CancellationToken, Task> operation, CancellationToken ct) {
        try {
            await _transitionLock.WaitAsync(ct);
        } catch (OperationCanceledException) {
            return; // Shutting down.
        }
        try {
            await operation(ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Shutting down mid-operation.
        } catch (Exception ex) {
            _logger.LogError(ex, "Proxy transition failed; it will be retried on the next settings change, route change or deploy.");
        } finally {
            _transitionLock.Release();
        }
    }

    /// <summary>
    /// Full reconcile: join Watchtower's own container to every stack's ingress network, then project
    /// the route table. Best-effort — logs and returns on failure, since route CRUD and deploys
    /// re-drive the relevant parts afterwards.
    /// </summary>
    /// <remarks>
    /// The two halves are independent on purpose, and the network half is the one that is allowed to
    /// fail. An unreachable Docker daemon used to take the projection down with it, which is far worse
    /// than a missing upstream hop: an empty route table makes every routed host fall through to
    /// Watchtower's own pipeline, so a tenant domain answers with Watchtower's UI — over the tenant's
    /// own certificate — while every status surface still reports the proxy as healthy.
    /// </remarks>
    internal async Task ReconcileAsync(CancellationToken ct) {
        try {
            // Watchtower *is* the proxy here, so the "proxy container" ProxyIngressNetworks joins to
            // each ingress network is its own.
            var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
            if (string.IsNullOrWhiteSpace(hostname))
                // Not fatal: the route table and the certificate bookkeeping are Watchtower's own state
                // and must not depend on Docker being reachable. Only the upstream hop would fail.
                _logger.LogWarning(
                    "HOSTNAME unset; cannot join Watchtower to the ingress networks. Running outside Docker?");
            else
                try {
                    await _networks.ConnectAllRoutedContainersAsync(hostname, ct);
                } catch (Exception ex) when (!ct.IsCancellationRequested) {
                    _logger.LogWarning(
                        ex,
                        "Joining the ingress networks failed; routes are projected anyway and the upstream hop "
                        + "will be retried on the next deploy or route change.");
                }

            await ApplyAsync(ct);
            var snapshot = _table.Current;
            _logger.LogInformation(
                "In-process reverse proxy reconciled ({Routes} routes, {TlsHosts} TLS hosts).",
                snapshot.Count, snapshot.TlsHosts.Count);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Shutting down.
        } catch (Exception ex) {
            _logger.LogError(ex, "In-process reverse-proxy reconcile failed; will be retried on the next route change or deploy.");
        }
    }

    /// <summary>
    /// Runtime disable (or a switch to another provider): empties the route table and stops asking for
    /// certificates. What is on disk stays there — re-enabling later must not re-hit the CA's rate
    /// limits — and the HTTPS listener the host bound at startup stays bound but matches nothing,
    /// because a listener is a process-lifetime thing and a settings toggle is not.
    /// </summary>
    private Task TeardownAsync(CancellationToken ct) {
        _table.Replace(ProxyRouteTableSnapshot.Empty);
        _certs.SetDesiredHosts([]);
        _logger.LogInformation(
            "In-process reverse proxy disabled at runtime: no routes are served. Issued certificates are "
            + "kept on disk and the HTTPS listener stays bound but idle.");
        return Task.CompletedTask;
    }

    // ── Public operations (called by handlers and the deploy pipeline) ─────────

    /// <summary>
    /// Projects the route table into the in-memory routing table and the desired-certificate set.
    /// Best-effort: never throws, so a projection hiccup cannot fail the route CRUD or deploy that
    /// triggered it.
    /// </summary>
    /// <remarks>Virtual for the same reason <see cref="CaddyManager.ApplyAsync"/> is: it returns
    /// nothing and no-ops while the provider is inactive, so a test double is the only way to observe
    /// that a re-projection was asked for.</remarks>
    public virtual async Task ApplyAsync(CancellationToken ct = default) {
        try {
            // Inside the guard on purpose: the certificate manager is an interface, and the ACME
            // implementation replacing the no-op could throw from either call. "Never throws" has to
            // hold on the inactive path too — it is reached from route CRUD and from teardown.
            if (!Enabled) {
                _table.Replace(ProxyRouteTableSnapshot.Empty);
                _certs.SetDesiredHosts([]);
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var routes = await db.Routes.AsNoTracking().Include(r => r.Stack).ToListAsync(ct);
            // Watchtower's own hostnames are rows in that table like any other (ADR-0023), and the
            // projection marks them Local; the dispatch middleware hands those to Watchtower's own
            // pipeline instead of forwarding them.
            var sites = ProxySiteProjection.Project(routes, _options.CurrentValue.Auth);

            var snapshot = ProxyRouteTable.From(sites);
            _table.Replace(snapshot);
            _certs.SetDesiredHosts(snapshot.TlsHosts);
            // Every served host has a row now, Watchtower's own included, so every one of them reports a
            // certificate status on the Routes page.
            await _routeStatus.MarkPendingAsync(
                snapshot.Rows.Where(r => r.RouteId is not null && r.Tls).Select(r => r.Host), ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to project the route table for the in-process proxy; will be retried on the next change.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Drops the certificate and account material held for the hostname. This is the one path that
    /// deletes: an operator removing a route has said the domain is gone, whereas a route merely
    /// disappearing from the desired set has not.
    /// </remarks>
    public async Task ForgetDomainAsync(string domain, string? actor, CancellationToken ct = default) {
        if (!Enabled) return;
        // Deliberately unguarded: the interface contract is that this throws when the caller's
        // specific external change did not happen.
        await _certs.ForgetHostAsync(domain.Trim().ToLowerInvariant(), ct);
        await ApplyAsync(ct);
    }

    /// <summary>
    /// Joins the stack's routed service containers — and Watchtower itself — to the stack's ingress
    /// network. Best-effort: never throws.
    /// </summary>
    public async Task ConnectStackAsync(int stackId, CancellationToken ct = default) {
        if (!Enabled) return;
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(hostname)) {
            _logger.LogWarning(
                "HOSTNAME unset; cannot join Watchtower to the ingress network of stack {StackId}. Running outside Docker?",
                stackId);
            return;
        }
        try {
            await _networks.ConnectStackServicesAsync(stackId, hostname, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to connect stack {StackId} services to its ingress network.", stackId);
        }
    }

    /// <summary>
    /// True when the provider is active and the host actually bound HTTPS. There is no container to
    /// inspect: "running" for the in-process proxy means the listener came up (see
    /// <see cref="YarpListenerState"/>).
    /// </summary>
    public Task<bool> IsRunningAsync(CancellationToken ct = default) =>
        Task.FromResult(Enabled && _listener.HttpsBound);
}
