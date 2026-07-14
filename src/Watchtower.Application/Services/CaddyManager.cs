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
/// a singleton. No-op unless <c>Proxy:Enabled</c> is set.
/// </summary>
public sealed class CaddyManager : IHostedService, IDisposable {
    public const string ControlNetwork = "watchtower-control";
    // Each stack gets its own ingress network shared only with Caddy, so tenants are isolated at L2
    // (a compromised tenant cannot reach another tenant's containers).
    private const string IngressNetworkPrefix = "watchtower-ingress-";
    private const string CaddyContainerName = "watchtower-caddy";
    private const string CaddyAlias = "watchtower-caddy";
    private const string SelfAlias = "watchtower";
    private const int AdminPort = 2019;
    private const string ManagedLabelKey = "com.watchtower.managed";
    private const string ComposeProjectLabel = "com.docker.compose.project";
    private const string ComposeServiceLabel = "com.docker.compose.service";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DockerEngineClient _docker;
    private readonly ProxyOptions _proxy;
    private readonly ILogger<CaddyManager> _logger;
    private readonly HttpClient _admin;
    private readonly CancellationTokenSource _cts = new();
    private Task? _reconcileTask;

    public CaddyManager(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        IOptions<WatchtowerOptions> options,
        ILogger<CaddyManager> logger) {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _proxy = options.Value.Proxy;
        _logger = logger;
        // Reached over the control network by the caddy container's DNS alias.
        _admin = new HttpClient { BaseAddress = new Uri($"http://{CaddyAlias}:{AdminPort}") };
    }

    public bool Enabled => _proxy.Enabled;

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!_proxy.Enabled) {
            _logger.LogInformation("Reverse proxy disabled (Proxy:Enabled=false); skipping Caddy setup.");
            return Task.CompletedTask;
        }
        // Reconcile off the startup path so a slow image pull never blocks host startup.
        _reconcileTask = Task.Run(() => ReconcileAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        await _cts.CancelAsync();
        if (_reconcileTask is not null)
            await Task.WhenAny(_reconcileTask, Task.Delay(Timeout.Infinite, cancellationToken));
    }

    public void Dispose() {
        _cts.Dispose();
        _admin.Dispose();
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
        } catch (OperationCanceledException) {
            // Shutting down.
        } catch (Exception ex) {
            _logger.LogError(ex, "Reverse-proxy reconcile failed; will be retried on the next route change or deploy.");
        }
    }

    // ── Public operations (called by handlers and the deploy pipeline) ─────────

    /// <summary>
    /// Renders the Caddyfile from the current route table and pushes it to Caddy for a reload.
    /// Best-effort: never throws, so a proxy hiccup can't fail the route CRUD or deploy that triggered it.
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default) {
        if (!_proxy.Enabled) return;
        try {
            var sites = await LoadSitesAsync(ct);
            // Caddy reaches Watchtower over the control network by the "watchtower" alias; the app listens
            // on :8080 inside the container. The ask endpoint gates on-demand certs to known domains.
            var askUrl = $"http://{SelfAlias}:8080/api/proxy/ask";
            var caddyfile = CaddyConfigBuilder.Build(sites, new CaddyGlobals(_proxy.AdminEmail, AdminPort, askUrl));
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
        if (!_proxy.Enabled) return;
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
        if (!_proxy.Enabled) return false;
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

        _logger.LogInformation("Pulling {Image}", _proxy.CaddyImage);
        await _docker.PullImageAsync(_proxy.CaddyImage, ct: ct);

        var body = new DockerCreateContainerBody {
            Image = _proxy.CaddyImage,
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
        return routes
            .Where(r => r.Stack is not null)
            .Select(r => new CaddySite(
                r.Domain,
                EdgeAlias(r.Stack!.ComposeProjectName, r.ServiceName),
                r.ContainerPort,
                r.TlsEnabled,
                // Customer-owned domains use on-demand TLS; managed subdomains are issued proactively.
                OnDemand: r.Kind == DomainKind.Custom))
            .ToList();
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
