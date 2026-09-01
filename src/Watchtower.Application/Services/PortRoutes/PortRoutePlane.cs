using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.InternalCa;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Services.PortRoutes;

/// <summary>
/// Everything a port-bound route needs, in one place and behind one gate — <c>Proxy:Enabled</c>
/// (ADR-0033, and the addendum that put it here).
/// </summary>
/// <remarks>
/// <para>
/// A port route is a TLS listener on Watchtower's <em>own</em> container, forwarded to a stack service
/// over that stack's ingress network. Nothing about that is a property of the proxy provider: a sibling
/// Caddy container and a Cloudflare Tunnel terminate the public <em>domains</em>, and neither has an
/// opinion about a port Watchtower binds inside itself. So this plane is not an
/// <see cref="IProxyProvider"/> and does not route by <c>Proxy:Provider</c>; it runs whenever the proxy
/// is on, alongside whichever provider serves the domains. Under <c>yarp</c> it runs alongside
/// <see cref="YarpProxyProvider"/>, which keeps the host half of the same route table.
/// </para>
/// <para>
/// What it owns, and why it is one class rather than four collaborators: the four steps are one
/// statement made in four places. The rows say which ports are routed
/// (<see cref="ProxySiteProjection.ProjectPortRoutes"/> into the port half of
/// <see cref="ProxyRouteTable"/>), the setting says which listeners exist
/// (<see cref="WatchtowerSettingPaths.ProxyPortRoutesPorts"/>, read by
/// <see cref="ProxyIngressKestrelConfiguration"/> before the host is built), the internal CA says what
/// those listeners present (<see cref="InternalCertificateService"/>), and the Docker network says
/// where the upstream is (<see cref="ProxyIngressNetworks"/>). Splitting them would mean four things
/// that have to be driven in the same order by every caller.
/// </para>
/// <para>
/// The shape is <see cref="YarpProxyProvider"/>'s: a singleton that is also an
/// <see cref="IHostedService"/>, one transition lock, a background reconcile off the startup path
/// (joining ingress networks talks to the Docker daemon and a slow daemon must not hold startup up),
/// and a subscription to <see cref="ProxyChangeSignal"/> so a route written on another instance is
/// re-projected here. The signal subscription is this plane's own, taken out the same way the provider
/// takes its own: two independent watchers on one key, each debounced, rather than one watcher that
/// has to know about both.
/// </para>
/// </remarks>
public class PortRoutePlane : IHostedService, IDisposable {
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProxyIngressNetworks _networks;
    private readonly ProxyRouteTable _table;
    private readonly InternalCertificateService _internalCerts;
    private readonly ProxyChangeSignal _signal;
    private readonly IOptionsMonitor<WatchtowerOptions> _options;
    private readonly ILogger<PortRoutePlane> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable? _optionsSubscription;
    private IDisposable? _signalSubscription;
    // Serializes the passes (startup reconcile, a runtime enable/disable, a route change) so a toggle
    // flipped twice in quick succession cannot interleave two projections of the same table.
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private readonly object _appliedGate = new();
    // Whether the plane was serving on the last options change, so OnChange can tell an enable from a
    // disable from a setting that has nothing to do with it.
    private bool _applied;
    private Task? _reconcileTask;

    public PortRoutePlane(
        IServiceScopeFactory scopeFactory,
        ProxyIngressNetworks networks,
        ProxyRouteTable table,
        InternalCertificateService internalCerts,
        ProxyChangeSignal signal,
        IOptionsMonitor<WatchtowerOptions> options,
        ILogger<PortRoutePlane> logger) {
        _scopeFactory = scopeFactory;
        _networks = networks;
        _table = table;
        _internalCerts = internalCerts;
        _signal = signal;
        _options = options;
        _logger = logger;
        _applied = options.CurrentValue.Proxy.Enabled;
        _optionsSubscription = options.OnChange(o => OnProxyOptionsChanged(o.Proxy.Enabled));
    }

    /// <summary>
    /// Active whenever the reverse proxy is enabled — deliberately <em>not</em> gated on the provider.
    /// See the class remarks: a port route's listener is Watchtower's own.
    /// </summary>
    private bool Enabled => _options.CurrentValue.Proxy.Enabled;

    public Task StartAsync(CancellationToken cancellationToken) {
        // Registered before the early return below, and whether or not the proxy is on right now: the
        // toggle is a Settings page away, and an instance that starts disabled still has to notice the
        // route changes that happen after somebody enables it. ApplyAsync no-ops while disabled, so a
        // signal on such an instance costs one options read.
        _signalSubscription ??= _signal.Watch(ApplyAsync);

        if (!Enabled) {
            _logger.LogInformation(
                "Port routes inactive (the reverse proxy is disabled); skipping setup. They can be "
                + "enabled at runtime from Settings.");
            return Task.CompletedTask;
        }
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
        _signalSubscription?.Dispose();
        _cts.Dispose();
        _transitionLock.Dispose();
    }

    // ── Public operations (called by the router, and by the host) ──────────────

    /// <summary>
    /// Projects the port-bound routes: the port half of the route table, the listener setting, and the
    /// certificate those listeners present. Best-effort — never throws, so a projection hiccup cannot
    /// fail the route CRUD or deploy that triggered it.
    /// </summary>
    /// <remarks>
    /// Virtual for the same reason <see cref="YarpProxyProvider.ApplyAsync"/> is: it returns nothing and
    /// no-ops while the proxy is off, so a test double is the only way to observe that a re-projection
    /// was asked for.
    /// </remarks>
    public virtual async Task ApplyAsync(CancellationToken ct = default) {
        try {
            if (!Enabled) {
                // Only this plane's half. The host half is the provider's, and emptying it here would
                // take every domain route down on a settings write the provider is about to act on
                // itself.
                _table.PublishPortRoutes([]);
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var portSites =
                ProxySiteProjection.ProjectPortRoutes(await PortRoutesAsync(scope.ServiceProvider, ct));
            _table.PublishPortRoutes(portSites);

            // The listeners follow from here and from nowhere else. Route CRUD, a stack delete cascading
            // its routes away and the cross-instance signal all arrive at this one method, so writing the
            // setting here is what makes "the rows say so" and "a socket is bound" the same statement.
            await WriteListenerPortsAsync(scope.ServiceProvider, _table.Current.PortRoutePorts, ct);

            // Last, and after the setting: a route that has just gained a listener needs the certificate
            // that listener presents, and the instance the operator is talking to is the one that has to
            // make their new route work. Cheap and idempotent when nothing moved.
            await _internalCerts.EnsureAsync(ct);
        } catch (Exception ex) {
            _logger.LogWarning(
                ex, "Failed to project the port routes; will be retried on the next change.");
        }
    }

    /// <summary>
    /// Joins Watchtower's own container — and the stack's port-routed service containers — to the
    /// stack's ingress network, which is the hop a port route's listener forwards over. Best-effort:
    /// never throws.
    /// </summary>
    /// <remarks>
    /// Port-bound rows only. Under Caddy or Cloudflare the stack's <em>domain</em> routes are served by
    /// that provider's own container over the same network, and joining Watchtower to a network it has
    /// no listener for would be exposure bought for nothing. Under yarp the provider joins the same
    /// network for the same stack a moment earlier or later, which costs nothing:
    /// <see cref="DockerEngineClient.ConnectContainerAsync"/> reads the daemon's "endpoint already
    /// exists" 403 as success.
    /// </remarks>
    public virtual async Task ConnectStackAsync(int stackId, CancellationToken ct = default) {
        if (!Enabled) return;
        if (SelfContainer(stackId) is not { } self) return;
        try {
            await _networks.ConnectStackPortRoutedServicesAsync(stackId, self, ct);
        } catch (Exception ex) {
            _logger.LogWarning(
                ex, "Failed to connect the port-routed services of stack {StackId} to its ingress network.",
                stackId);
        }
    }

    // ── Startup reconcile ─────────────────────────────────────────────────────

    /// <summary>
    /// Full pass: join Watchtower to the ingress network of every stack that has a port route, then
    /// project. Best-effort — logs and returns on failure, since route CRUD and deploys re-drive the
    /// relevant parts afterwards.
    /// </summary>
    /// <remarks>
    /// The two halves are independent on purpose, and the network half is the one that is allowed to
    /// fail: an unreachable Docker daemon means a port route that answers 502, whereas letting it take
    /// the projection down would mean a port route's listener that is not bound at all — or, worse, one
    /// that is bound and whose row the table has forgotten.
    /// </remarks>
    internal async Task ReconcileAsync(CancellationToken ct) {
        try {
            if (SelfContainer(stackId: null) is { } self)
                try {
                    await _networks.ConnectAllPortRoutedContainersAsync(self, ct);
                } catch (Exception ex) when (!ct.IsCancellationRequested) {
                    _logger.LogWarning(
                        ex,
                        "Joining the ingress networks of the port-routed stacks failed; the routes are "
                        + "projected anyway and the upstream hop will be retried on the next deploy or "
                        + "route change.");
                }

            await ApplyAsync(ct);
            var ports = _table.Current.PortRoutePorts;
            _logger.LogInformation(
                "Port routes reconciled ({Count} listener(s): {Ports}).",
                ports.Count, ports.Count == 0 ? "none" : string.Join(", ", ports.Order()));
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Shutting down.
        } catch (Exception ex) {
            _logger.LogError(
                ex, "Port-route reconcile failed; will be retried on the next route change or deploy.");
        }
    }

    // ── Runtime enable/disable (settings-driven) ──────────────────────────────

    private void OnProxyOptionsChanged(bool enabled) {
        bool was;
        lock (_appliedGate) {
            was = _applied;
            _applied = enabled;
        }
        if (was == enabled) return;
        _logger.LogInformation("Port routes {Transition} at runtime.", enabled ? "enabled" : "disabled");
        // Enabling is the full pass, because the ingress networks have to be joined before anything can
        // be forwarded; disabling is just a projection, which empties the port half and the setting with
        // it. No separate teardown: ApplyAsync's disabled path is the teardown.
        Func<CancellationToken, Task> operation = enabled ? ReconcileAsync : ApplyAsync;
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
            _logger.LogError(
                ex, "Port-route transition failed; it will be retried on the next settings change, route "
                + "change or deploy.");
        } finally {
            _transitionLock.Release();
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>The port-bound rows, with the stack the projection names their upstream from.</summary>
    private static async Task<List<Route>> PortRoutesAsync(IServiceProvider services, CancellationToken ct) {
        var db = services.GetRequiredService<WatchtowerDbContext>();
        return await db.Routes.AsNoTracking()
            .Where(r => r.Binding == RouteBinding.Port)
            .Include(r => r.Stack)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Publishes the listen ports into the setting the Kestrel projection reads (ADR-0033), unless it
    /// already says exactly that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compare first, always. Every instance runs this on every pass — on startup, on each route change
    /// and on each cross-instance signal — and an unconditional write would be a settings write per pass
    /// per instance, each of them bumping the store's own change token for a value nobody moved.
    /// </para>
    /// <para>
    /// The comparison is between two <see cref="PortRouteListeners.Format"/> renderings rather than
    /// between two sets, so "the same ports written in another order" is not a change either. This is not
    /// <see cref="WatchtowerSettingPaths.ProxyRoutesVersion"/>, so writing it wakes no watcher and cannot
    /// loop; what it does wake is the configuration reload, which is the point.
    /// </para>
    /// </remarks>
    private async Task WriteListenerPortsAsync(
        IServiceProvider services, IReadOnlyCollection<int> ports, CancellationToken ct) {
        var settings = services.GetRequiredService<ISettingsManager>();
        var wanted = PortRouteListeners.Format(ports);
        var stored = await settings.GetStringAsync(
            WatchtowerSettingPaths.ProxyPortRoutesPorts, SettingsScope.Global, ct);
        // Re-rendered rather than compared raw: a value an operator or an older build left in another
        // spelling would otherwise be rewritten on every pass forever.
        if (string.Equals(PortRouteListeners.Format(PortRouteListeners.Parse(stored)), wanted, StringComparison.Ordinal))
            return;

        await settings.SetStringAsync(
            WatchtowerSettingPaths.ProxyPortRoutesPorts, wanted, SettingsScope.Global,
            expectedVersion: null, ct);
        _logger.LogInformation(
            "Port route listeners are now {Ports}.", wanted.Length == 0 ? "none" : wanted);
    }

    /// <summary>
    /// Watchtower's own container id, as Docker knows it, or null with a warning. The proxy container
    /// that joins a port-routed stack's ingress network is always this one — that is what a port route
    /// <em>is</em> — so there is nothing here to resolve from the provider.
    /// </summary>
    private string? SelfContainer(int? stackId) {
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (!string.IsNullOrWhiteSpace(hostname)) return hostname;
        // Not fatal: the route table, the listener setting and the certificate are Watchtower's own
        // state and must not depend on Docker being reachable. Only the upstream hop would fail.
        if (stackId is { } id)
            _logger.LogWarning(
                "HOSTNAME unset; cannot join Watchtower to the ingress network of stack {StackId}. "
                + "Running outside Docker?", id);
        else
            _logger.LogWarning(
                "HOSTNAME unset; cannot join Watchtower to the port-routed stacks' ingress networks. "
                + "Running outside Docker?");
        return null;
    }
}
