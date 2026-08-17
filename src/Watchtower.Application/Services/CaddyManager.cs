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
/// Owns the built-in Caddy reverse proxy. Watchtower's <c>routes</c> table is the source of truth;
/// this service:
/// <list type="number">
///   <item>ensures two Docker networks exist — <c>watchtower-control</c> (Caddy ↔ Watchtower, carries the
///   admin API off the public path) and <c>watchtower-edge</c> (Caddy → routed service containers);</item>
///   <item>ensures a managed <c>caddy:2</c> container is running with 80/443 published and its data/config
///   volumes mounted;</item>
///   <item>joins each routed service's container to the edge network under a stable DNS alias;</item>
///   <item>renders a Caddyfile from the route table and pushes it to Caddy's admin API for a zero-downtime
///   reload.</item>
/// </list>
/// It is a singleton (injected into handlers and the deploy queue) and an <see cref="IHostedService"/>
/// so the whole topology is reconciled on startup. All DB access opens short-lived scopes since this is
/// a singleton. No-op while <c>Proxy:Enabled</c> is off — and that toggle is runtime-switchable via the
/// settings store: an options change triggers a reconcile (enable), a container teardown (disable), or a
/// config refresh (e.g. AdminEmail), with no restart.
/// </summary>
public class CaddyManager : IHostedService, IDisposable {
    public const string ControlNetwork = "watchtower-control";
    // Each stack gets its own ingress network shared only with Caddy, so tenants are isolated at L2
    // (a compromised tenant cannot reach another tenant's containers).
    private const string IngressNetworkPrefix = "watchtower-ingress-";
    private const string CaddyContainerName = "watchtower-caddy";
    private const string CaddyAlias = "watchtower-caddy";
    private const string SelfAlias = "watchtower";
    /// <summary>Port Watchtower listens on inside its container; where Caddy reaches it on the control network.</summary>
    private const int SelfPort = 8080;
    private const int AdminPort = 2019;
    private const string ManagedLabelKey = "com.watchtower.managed";
    private const string ComposeProjectLabel = "com.docker.compose.project";
    private const string ComposeServiceLabel = "com.docker.compose.service";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DockerEngineClient _docker;
    private readonly IOptionsMonitor<WatchtowerOptions> _options;
    private readonly ILogger<CaddyManager> _logger;
    private readonly HttpClient _admin;
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable? _optionsSubscription;
    // Serializes the topology operations (startup reconcile, runtime enable/disable, refresh) so a
    // toggle flipped twice in quick succession can't interleave a teardown with a reconcile.
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private readonly object _appliedGate = new();
    // The proxy settings the manager last acted on; OnChange diffs against this to decide a transition.
    private ProxyOptions _applied;
    private Task? _reconcileTask;

    public CaddyManager(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        IOptionsMonitor<WatchtowerOptions> options,
        ILogger<CaddyManager> logger) {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _options = options;
        _logger = logger;
        // Proxy and Auth settings are both read live: the settings store re-binds them at runtime, and
        // the OnChange subscription below turns a Proxy:Enabled flip into a reconcile or teardown, an
        // email change into a config refresh — no restart. (A CaddyImage change only applies when the
        // container is next recreated: a healthy running container is reused as-is.)
        _applied = options.CurrentValue.Proxy;
        _optionsSubscription = options.OnChange(o => OnProxyOptionsChanged(o.Proxy));
        // Reached over the control network by the caddy container's DNS alias.
        _admin = new HttpClient { BaseAddress = new Uri($"http://{CaddyAlias}:{AdminPort}") };
    }

    public bool Enabled => _options.CurrentValue.Proxy.Enabled;

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!Enabled) {
            _logger.LogInformation("Reverse proxy disabled (Proxy:Enabled=false); skipping Caddy setup. It can be enabled at runtime from Settings.");
            return Task.CompletedTask;
        }
        // Reconcile off the startup path so a slow image pull never blocks host startup.
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
        _admin.Dispose();
        _transitionLock.Dispose();
    }

    // ── Runtime enable/disable (settings-driven) ──────────────────────────────

    /// <summary>What a proxy-options change means for the managed topology.</summary>
    internal enum ProxyTransition {
        /// <summary>Nothing relevant changed.</summary>
        None,
        /// <summary>Disabled → enabled: full reconcile (networks, container, routes, config).</summary>
        Start,
        /// <summary>Enabled → disabled: stop and remove the managed Caddy container.</summary>
        Stop,
        /// <summary>Still enabled but a value changed (e.g. AdminEmail): re-render and push the config.</summary>
        Refresh,
    }

    /// <summary>Pure decision seam for the options-change reaction, split out for tests.</summary>
    internal static ProxyTransition DecideTransition(ProxyOptions was, ProxyOptions now) {
        if (was == now) return ProxyTransition.None;
        if (now.Enabled && !was.Enabled) return ProxyTransition.Start;
        if (!now.Enabled && was.Enabled) return ProxyTransition.Stop;
        return now.Enabled ? ProxyTransition.Refresh : ProxyTransition.None;
    }

    /// <summary>
    /// Reacts to a runtime change of the proxy settings (the settings store re-binding the options).
    /// The options monitor may fire multiple times per logical change and for unrelated Watchtower
    /// options; diffing against the last-applied record filters that noise.
    /// </summary>
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
    /// Runtime disable: stops and removes the managed Caddy container. The control/ingress networks and
    /// the <c>caddy_data</c> volume (issued certificates) are deliberately kept, so re-enabling later is
    /// cheap and does not re-hit the ACME CA's rate limits.
    /// </summary>
    private async Task TeardownAsync(CancellationToken ct) {
        try {
            await _docker.StopContainerAsync(CaddyContainerName, ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Stopping the Caddy container failed (it may not exist).");
        }
        try {
            await _docker.RemoveContainerAsync(CaddyContainerName, ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Removing the Caddy container failed (it may not exist).");
        }
        _logger.LogInformation(
            "Reverse proxy disabled at runtime: the managed Caddy container was stopped and removed. " +
            "Networks and the caddy_data volume (certificates) are kept for re-enabling.");
    }

    /// <summary>
    /// Full startup reconcile: networks, self-join, the Caddy container, then wire existing routed
    /// containers and push the current config. Best-effort — logs and returns on failure (the daemon
    /// may be briefly unavailable); route CRUD and deploys re-drive the relevant parts afterwards.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken ct) {
        try {
            await EnsureNetworkAsync(ControlNetwork, ct);
            await JoinSelfToControlAsync(ct);
            await EnsureCaddyContainerAsync(ct);
            await ConnectAllRoutedContainersAsync(ct);
            await ApplyAsync(ct);
            _logger.LogInformation("Reverse proxy reconciled.");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Shutting down. Guarded on the token: an HttpClient timeout surfaces as a
            // TaskCanceledException too, and must reach the error log below.
        } catch (Exception ex) {
            _logger.LogError(ex, "Reverse-proxy reconcile failed; will be retried on the next route change or deploy.");
        }
    }

    // ── Public operations (called by handlers and the deploy pipeline) ─────────

    /// <summary>
    /// Renders the Caddyfile from the current route table and pushes it to Caddy for a reload.
    /// Best-effort: never throws, so a proxy hiccup can't fail the route CRUD or deploy that triggered it.
    /// </summary>
    /// <remarks>
    /// Virtual so tests can observe that a reload was requested. There is no other way to see it: the
    /// method returns nothing and no-ops entirely while the proxy is disabled, which is how it runs in
    /// every test host — yet "the proxy stopped serving the deleted tenant's domain" is precisely the
    /// part of teardown worth pinning.
    /// </remarks>
    public virtual async Task ApplyAsync(CancellationToken ct = default) {
        if (!Enabled) return;
        try {
            var sites = await LoadSitesAsync(ct);
            // Caddy reaches Watchtower over the control network by the "watchtower" alias; the app listens
            // on :8080 inside the container. The ask endpoint gates on-demand certs to known domains, and
            // the same address carries the forward-auth and callback traffic for protected sites.
            var askUrl = $"http://{SelfAlias}:{SelfPort}/api/proxy/ask";
            var caddyfile = CaddyConfigBuilder.Build(
                sites, new CaddyGlobals(_options.CurrentValue.Proxy.AdminEmail, AdminPort, askUrl, $"{SelfAlias}:{SelfPort}"));
            await PushConfigAsync(caddyfile, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to apply Caddy config; will be retried on the next change.");
        }
    }

    /// <summary>
    /// Joins the routed service container(s) of a stack to the edge network under a stable alias.
    /// Best-effort: never throws.
    /// </summary>
    public async Task ConnectStackAsync(int stackId, CancellationToken ct = default) {
        if (!Enabled) return;
        try {
            List<(string Project, string Service)> targets;
            await using (var scope = _scopeFactory.CreateAsyncScope()) {
                var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
                targets = await db.Routes.AsNoTracking()
                    .Where(r => r.StackId == stackId)
                    .Include(r => r.Stack)
                    .Select(r => new { r.Stack!.ComposeProjectName, r.ServiceName })
                    .Distinct()
                    .Select(x => new ValueTuple<string, string>(x.ComposeProjectName, x.ServiceName))
                    .ToListAsync(ct);
            }
            foreach (var (project, service) in targets)
                await ConnectServiceAsync(stackId, project, service, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to connect stack {StackId} services to its ingress network.", stackId);
        }
    }

    /// <summary>True when the managed Caddy container reports a running state.</summary>
    public async Task<bool> IsCaddyRunningAsync(CancellationToken ct = default) {
        if (!Enabled) return false;
        try {
            var details = await _docker.InspectContainerAsync(CaddyContainerName, ct);
            return details.State?.Status == "running";
        } catch {
            return false;
        }
    }

    // ── Reconcile steps ───────────────────────────────────────────────────────

    private async Task EnsureNetworkAsync(string name, CancellationToken ct) {
        var networks = await _docker.ListNetworksAsync(ct);
        if (networks.Any(n => n.Name == name)) return;
        _logger.LogInformation("Creating proxy network {Network}", name);
        await _docker.CreateNetworkAsync(name, new Dictionary<string, string> { [ManagedLabelKey] = "network" }, ct);
    }

    private async Task JoinSelfToControlAsync(CancellationToken ct) {
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(hostname)) {
            _logger.LogWarning("HOSTNAME unset; cannot join Watchtower to the control network. Running outside Docker?");
            return;
        }
        await _docker.ConnectContainerAsync(ControlNetwork, hostname, [SelfAlias], ct);
    }

    private async Task EnsureCaddyContainerAsync(CancellationToken ct) {
        // Reuse a healthy container; otherwise remove any stale one and recreate.
        try {
            var details = await _docker.InspectContainerAsync(CaddyContainerName, ct);
            if (details.State?.Status == "running") {
                _logger.LogInformation("Caddy container already running; reusing it.");
                return;
            }
            _logger.LogInformation("Removing stale Caddy container (status {Status})", details.State?.Status);
            await _docker.RemoveContainerAsync(CaddyContainerName, ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "No existing Caddy container found; creating a new one.");
        }

        var image = _options.CurrentValue.Proxy.CaddyImage;
        _logger.LogInformation("Pulling {Image}", image);
        await _docker.PullImageAsync(image, ct: ct);

        var body = new DockerCreateContainerBody {
            Image = image,
            // Start with a blank config; CADDY_ADMIN puts the admin API on the control network so we can
            // push the real config via /load. Overriding Cmd to just "run" avoids loading the image's
            // default Caddyfile (which would bind admin to localhost only).
            Cmd = ["run"],
            Env = [$"CADDY_ADMIN=0.0.0.0:{AdminPort}"],
            Labels = new Dictionary<string, string> { [ManagedLabelKey] = "caddy" },
            ExposedPorts = new Dictionary<string, DockerEmptyObject> {
                ["80/tcp"] = new(), ["443/tcp"] = new(), ["443/udp"] = new(),
            },
            HostConfig = new DockerCreateHostConfig {
                Binds = ["caddy_data:/data", "caddy_config:/config"],
                PortBindings = new Dictionary<string, List<DockerPortBinding>> {
                    ["80/tcp"] = [new DockerPortBinding { HostPort = "80" }],
                    ["443/tcp"] = [new DockerPortBinding { HostPort = "443" }],
                    ["443/udp"] = [new DockerPortBinding { HostPort = "443" }],
                },
                RestartPolicy = new DockerRestartPolicy { Name = "unless-stopped" },
            },
            NetworkingConfig = new DockerNetworkingConfig {
                EndpointsConfig = new Dictionary<string, DockerEndpointConfig> {
                    [ControlNetwork] = new DockerEndpointConfig { Aliases = [CaddyAlias] },
                },
            },
        };

        var id = await _docker.CreateContainerAsync(body, CaddyContainerName, ct);
        // Caddy joins each stack's ingress network on demand (EnsureStackNetworkAsync); it starts on the
        // control network only.
        await _docker.StartContainerAsync(id, ct);
        _logger.LogInformation("Started managed Caddy container {ShortId}", id.Length >= 12 ? id[..12] : id);
    }

    private async Task ConnectAllRoutedContainersAsync(CancellationToken ct) {
        List<(int StackId, string Project, string Service)> targets;
        await using (var scope = _scopeFactory.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            targets = await db.Routes.AsNoTracking()
                .Include(r => r.Stack)
                .Select(r => new { r.StackId, r.Stack!.ComposeProjectName, r.ServiceName })
                .Distinct()
                .Select(x => new ValueTuple<int, string, string>(x.StackId, x.ComposeProjectName, x.ServiceName))
                .ToListAsync(ct);
        }
        foreach (var (stackId, project, service) in targets)
            await ConnectServiceAsync(stackId, project, service, ct);
    }

    /// <summary>
    /// Ensures the stack's ingress network exists and Caddy is on it, then connects every container of a
    /// compose service to it under a stable alias.
    /// </summary>
    private async Task ConnectServiceAsync(int stackId, string project, string service, CancellationToken ct) {
        var network = await EnsureStackNetworkAsync(stackId, ct);
        var alias = EdgeAlias(project, service);
        var containers = await _docker.ListContainersByLabelsAsync(
            [$"{ComposeProjectLabel}={project}", $"{ComposeServiceLabel}={service}"], ct);
        if (containers.Count == 0) {
            _logger.LogDebug("No container found for {Project}/{Service}; nothing to connect yet.", project, service);
            return;
        }
        foreach (var c in containers) {
            try {
                await _docker.ConnectContainerAsync(network, c.Id, [alias], ct);
            } catch (Exception ex) {
                var shortId = c.Id.Length >= 12 ? c.Id[..12] : c.Id;
                _logger.LogWarning(ex, "Failed to connect {Container} ({Alias}) to {Network}", shortId, alias, network);
            }
        }
    }

    /// <summary>Creates the stack's ingress network if missing and joins Caddy to it; returns its name.</summary>
    private async Task<string> EnsureStackNetworkAsync(int stackId, CancellationToken ct) {
        var network = IngressNetworkPrefix + stackId;
        var networks = await _docker.ListNetworksAsync(ct);
        if (networks.All(n => n.Name != network)) {
            _logger.LogInformation("Creating ingress network {Network}", network);
            await _docker.CreateNetworkAsync(network, new Dictionary<string, string> { [ManagedLabelKey] = "ingress" }, ct);
        }
        // Idempotent: a 403 (already connected) is treated as success by ConnectContainerAsync.
        await _docker.ConnectContainerAsync(network, CaddyContainerName, aliases: null, ct);
        return network;
    }

    // ── Config rendering + push ────────────────────────────────────────────────

    private async Task<IReadOnlyList<CaddySite>> LoadSitesAsync(CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var routes = await db.Routes.AsNoTracking()
            .Include(r => r.Stack)
            .ToListAsync(ct);
        // Every realm's login page has to be served too, not just the operator one (design.md §13).
        var realmHosts = await scope.ServiceProvider
            .GetRequiredService<RealmResolver>()
            .AuthHostsAsync(ct);
        return ProjectSites(routes, _options.CurrentValue.Auth, realmHosts);
    }

    /// <summary>
    /// Projects the route table onto the site list, adding a Watchtower self-route for every login host
    /// that needs one. Split out from <see cref="LoadSitesAsync"/> as a pure function so the access-control
    /// decisions can be tested without a database or a Docker daemon.
    /// </summary>
    /// <remarks>
    /// A route is protected only when access control is switched on <em>and</em> the route asks for it, so
    /// turning <c>Auth:Enabled</c> off is a complete escape hatch: the next reconcile emits exactly the
    /// configuration this file produced before access control existed, whatever the route rows say.
    /// <para>
    /// The self-routes are the answer to the bootstrap problem in design.md §11 — a protected app redirects
    /// to its realm's login host, so that host has to be served before forward-auth is useful for anything.
    /// There are now N of them: the configured <c>Auth:Host</c> (the operator realm's, which is
    /// configuration rather than a row so authentication can always find its own login page) plus every
    /// realm's <see cref="Realm.AuthHost"/>.
    /// </para>
    /// <para>
    /// <b>The invariant: no realm's login host may sit behind its own gate.</b> None of these sites is ever
    /// <c>Protected</c> — putting a login page behind the forward-auth that redirects to that login page is
    /// a closed loop, and the only way out of it is the published port. An explicit <see cref="Route"/> row
    /// for one of those domains still renders, because the operator has said what they want that host to
    /// serve and silently shadowing it would be worse than honouring it, but it is force-unprotected
    /// whatever its <see cref="AccessMode"/> says. Watchtower authenticates its own UI natively (§2.5), so
    /// nothing is lost.
    /// </para>
    /// </remarks>
    /// <param name="routes">The route table.</param>
    /// <param name="auth">Access-control settings; <c>Host</c> is the operator realm's login host.</param>
    /// <param name="realmAuthHosts">
    /// Every non-system realm's non-null <see cref="Realm.AuthHost"/>. Required rather than defaulted:
    /// forgetting the realm hosts silently un-serves every realm's login page and re-gates any route on one
    /// of those domains, so on a projection this security-relevant it should be a compile error rather than
    /// an omission. Pass an empty list to mean "no realms". Blanks and duplicates are tolerated — this is a
    /// projection, not a validator, and the handlers are where a bad host is refused.
    /// </param>
    internal static List<CaddySite> ProjectSites(
        IReadOnlyList<Route> routes, AuthOptions auth, IReadOnlyList<string> realmAuthHosts) {
        var sites = routes
            .Where(r => r.Stack is not null)
            .Select(r => new CaddySite(
                r.Domain,
                EdgeAlias(r.Stack!.ComposeProjectName, r.ServiceName),
                r.ContainerPort,
                r.TlsEnabled,
                // Customer-owned domains use on-demand TLS; managed subdomains are issued proactively.
                OnDemand: r.Kind == DomainKind.Custom,
                Protected: auth.Enabled && r.AccessMode != AccessMode.Public,
                // Only read for a protected site; the route's mode decides which plaintext headers it forwards.
                Mode: r.IdentityHeaderMode))
            .ToList();

        if (!auth.Enabled) return sites;

        // One distinct entry per login host, ordered configuration-first so the operator realm's block is
        // the stable head of the list whatever the realms table happens to return.
        var loginHosts = new List<string>();
        foreach (var candidate in new[] { auth.Host }.Concat(realmAuthHosts)) {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var host = candidate.Trim().ToLowerInvariant();
            if (!loginHosts.Contains(host, StringComparer.Ordinal)) loginHosts.Add(host);
        }

        foreach (var host in loginHosts) {
            var existing = sites.FindIndex(s => string.Equals(s.Domain, host, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) sites.Add(new CaddySite(host, SelfAlias, SelfPort, Tls: true));
            else sites[existing] = sites[existing] with { Protected = false };
        }

        return sites;
    }

    /// <summary>POSTs the Caddyfile to the admin <c>/load</c> endpoint, retrying while Caddy boots.</summary>
    private async Task PushConfigAsync(string caddyfile, CancellationToken ct) {
        const int attempts = 12;
        for (var i = 1; i <= attempts; i++) {
            try {
                using var content = new StringContent(caddyfile, Encoding.UTF8);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/caddyfile");
                var response = await _admin.PostAsync("/load", content, ct);
                if (response.IsSuccessStatusCode) {
                    _logger.LogInformation("Pushed Caddy config ({Bytes} bytes).", caddyfile.Length);
                    return;
                }
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Caddy /load returned {Status}: {Error}", (int)response.StatusCode, err.Trim());
                return; // A non-success from a reachable admin is a config error, not a boot race — don't spin.
            } catch (HttpRequestException) when (i < attempts) {
                await Task.Delay(500, ct); // Admin not up yet — retry.
            }
        }
        _logger.LogError("Could not reach the Caddy admin API after {Attempts} attempts.", attempts);
    }

    /// <summary>Stable, collision-free DNS alias for a service on the edge network (unique per stack).</summary>
    private static string EdgeAlias(string project, string service) => $"{project}-{service}";
}
