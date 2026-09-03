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
/// The Cloudflare Tunnel proxy provider (ADR-0015). The <c>routes</c> table stays the source of truth;
/// on every reconcile this provider:
/// <list type="number">
///   <item>finds (or, in managed mode, creates) the remotely-managed tunnel by its configured name;</item>
///   <item>in managed mode, ensures a <c>cloudflared</c> container is running the tunnel — created and
///   supervised over the Docker socket exactly like the Caddy container; in unmanaged mode the operator
///   runs cloudflared and Watchtower touches no container (optionally connecting a named, operator-run
///   container to the ingress networks so the generated service URLs resolve);</item>
///   <item>joins each routed service's container to its per-stack ingress network under the stable
///   <c>{project}-{service}</c> alias (<see cref="ProxyIngressNetworks"/> — same topology as Caddy);</item>
///   <item>replaces the tunnel's ingress rules with a projection of the route table and upserts a
///   proxied CNAME (<c>{domain}</c> → <c>{tunnelId}.cfargotunnel.com</c>) per route.</item>
/// </list>
/// TLS terminates at Cloudflare's edge — no host ports, no ACME. Runtime-switchable like
/// <see cref="CaddyManager"/>: an options change starts, stops or refreshes the provider; disabling
/// (or switching provider) removes only the managed container — the tunnel and DNS records are kept,
/// so re-enabling is cheap and nothing public breaks that the operator didn't ask to break.
/// </summary>
public class CloudflareTunnelProvider : IHostedService, IProxyProvider, IDisposable {
    private const string CloudflaredContainerName = "watchtower-cloudflared";
    private const string ManagedLabelKey = ProxyIngressNetworks.ManagedLabelKey;

    /// <summary>Audit-trail category for every external write this provider performs.</summary>
    internal const string AuditCategory = "proxy.cloudflare";

    /// <summary>
    /// What a <see cref="RouteTarget.Watchtower"/> route's status says under this provider. Stated once so
    /// the reconcile and the tests read the same sentence.
    /// </summary>
    internal const string SelfRouteUnsupported =
        "Watchtower routes are not served by the Cloudflare provider yet; expose Watchtower through " +
        "Cloudflare's dashboard/Access. The hostname is still used as this realm's login address.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DockerEngineClient _docker;
    private readonly ProxyIngressNetworks _networks;
    private readonly CloudflareApiClient _api;
    private readonly AuditLog _audit;
    private readonly IOptionsMonitor<WatchtowerOptions> _options;
    private readonly ILogger<CloudflareTunnelProvider> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable? _optionsSubscription;
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private readonly object _appliedGate = new();
    private ProxyOptions _applied;
    private Task? _reconcileTask;

    public CloudflareTunnelProvider(
        IServiceScopeFactory scopeFactory,
        DockerEngineClient docker,
        ProxyIngressNetworks networks,
        CloudflareApiClient api,
        AuditLog audit,
        IOptionsMonitor<WatchtowerOptions> options,
        ILogger<CloudflareTunnelProvider> logger) {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _networks = networks;
        _api = api;
        _audit = audit;
        _options = options;
        _logger = logger;
        _applied = options.CurrentValue.Proxy;
        _optionsSubscription = options.OnChange(o => OnProxyOptionsChanged(o.Proxy));
    }

    /// <summary>Active only while the proxy is enabled AND cloudflare is the selected provider.</summary>
    public bool Enabled => IsActive(_options.CurrentValue.Proxy);

    private static bool IsActive(ProxyOptions o) => o.Enabled && o.ResolveProvider() == ProxyProviderKind.Cloudflare;

    internal static ProxyTransition DecideTransition(ProxyOptions was, ProxyOptions now) =>
        ProxyTransitions.Decide(IsActive(was), IsActive(now), was != now);

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!Enabled) return Task.CompletedTask;
        // Reconcile off the startup path so API/pull latency never blocks host startup.
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

    private void OnProxyOptionsChanged(ProxyOptions next) {
        ProxyTransition transition;
        lock (_appliedGate) {
            transition = DecideTransition(_applied, next);
            _applied = next;
        }
        if (transition == ProxyTransition.None) return;
        _logger.LogInformation("Cloudflare proxy settings changed at runtime ({Transition}).", transition);
        Func<CancellationToken, Task> operation = transition switch {
            ProxyTransition.Start => ReconcileAsync,
            ProxyTransition.Stop => TeardownAsync,
            _ => ReconcileAsync, // Refresh: config values changed — re-run the full projection, it is idempotent.
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
            _logger.LogError(ex, "Cloudflare tunnel transition failed; it will be retried on the next settings change, route change or deploy.");
        } finally {
            _transitionLock.Release();
        }
    }

    // ── IProxyProvider ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public virtual async Task ApplyAsync(CancellationToken ct = default) {
        if (!Enabled) return;
        try {
            var cf = _options.CurrentValue.Proxy.Cloudflare;
            if (Misconfigured(cf, out var why)) {
                _logger.LogWarning("Cloudflare tunnel not applied: {Reason}", why);
                return;
            }
            var tunnel = await EnsureTunnelAsync(cf, ct);
            if (tunnel is null) return;

            var all = await LoadRoutesAsync(ct);
            // Watchtower's own hostnames (ADR-0023) are not something this provider can serve: a tunnel
            // ingress rule pointing at Watchtower would publish the management plane through Cloudflare
            // with no gate in front of it, which is exactly the thing Cloudflare Access exists to do
            // properly. They are excluded from everything below and told to say so on the Routes page —
            // the hostname still matters, because it is the address the realm's protected apps redirect to.
            var selfRoutes = all.Where(r => r.Target == RouteTarget.Watchtower).ToList();
            if (selfRoutes.Count > 0)
                await SetRouteStatusAsync(selfRoutes.Select(r => r.Id), RouteStatus.Error, SelfRouteUnsupported, ct);

            // Port routes (ADR-0033) are excluded from everything below — a tunnel publishes hostnames
            // and a port route has none — but they are not an error here: their listeners are on
            // Watchtower's own container and PortRoutePlane serves them alongside the tunnel (ADR-0033
            // addendum), so their status is the internal CA's to write, not this provider's.

            var routes = all
                .Where(r => r.Target == RouteTarget.Service && r.Binding == RouteBinding.Domain)
                .ToList();
            // The hostnames of those routes, which is what every call below is keyed by. Projected once
            // so "a service route has a hostname" is stated in one place rather than at each use.
            var domains = routes.Select(r => r.Domain).OfType<string>().ToList();
            // Merge, don't replace: rules the operator made in the dashboard (hostnames Watchtower's
            // route table doesn't know) are preserved verbatim — the configurations endpoint is a
            // whole-config PUT, so without this a fresh Watchtower pointed at an existing tunnel
            // would wipe every public hostname it didn't create. Foreign rules can be adopted from
            // the Routes page (proxy.listCloudflareForeignRoutes), which moves them under the table.
            var existing = await _api.GetTunnelConfigurationAsync(cf.AccountId!, tunnel.Id, cf.ApiToken!, ct);
            var foreign = ForeignIngressRules(existing, domains);
            var ingress = MergeIngress(existing, ProjectIngress(routes), domains);
            // Skip the PUT (and its audit row) when the remote configuration already matches — a
            // reconcile that changed nothing is not a write.
            if (!ingress.SequenceEqual(existing)) {
                var detail = $"{ingress.Count - 1} hostname rule(s), {foreign.Count} foreign preserved";
                try {
                    await _api.PutTunnelConfigurationAsync(cf.AccountId!, tunnel.Id, ingress, cf.ApiToken!, ct);
                    await _audit.RecordAsync(AuditCategory, "tunnel.config.push", tunnel.Name, detail, ct: ct);
                } catch (Exception ex) {
                    await _audit.RecordAsync(AuditCategory, "tunnel.config.push", tunnel.Name, detail,
                        success: false, error: ex.Message, ct: ct);
                    await SetRouteStatusAsync(routes.Select(r => r.Id), RouteStatus.Error,
                        $"Tunnel configuration push failed: {ex.Message}", ct);
                    throw;
                }
                _logger.LogInformation("Pushed {Count} ingress rule(s) to tunnel {Tunnel}.", ingress.Count - 1, tunnel.Name);
            }

            // The outcome per hostname is known right here, so the route row says so — Active once its
            // CNAME points at the tunnel, Error with Cloudflare's own words when it does not — instead of
            // sitting at Pending with the reason only in the audit trail.
            var target = $"{tunnel.Id}.cfargotunnel.com";
            foreach (var domain in domains.Distinct(StringComparer.OrdinalIgnoreCase)) {
                var routeIds = routes.Where(r => string.Equals(r.Domain, domain, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id);
                try {
                    var upsert = await _api.UpsertDnsCnameAsync(cf.ZoneId!, domain, target, cf.ApiToken!, ct);
                    if (upsert != CloudflareDnsUpsert.Unchanged) {
                        await _audit.RecordAsync(AuditCategory,
                            upsert == CloudflareDnsUpsert.Created ? "dns.create" : "dns.update",
                            domain, $"proxied CNAME → {target}", ct: ct);
                    }
                    await SetRouteStatusAsync(routeIds, RouteStatus.Active, null, ct);
                } catch (Exception ex) {
                    // Per-domain best effort: one domain outside the configured zone must not stop the rest.
                    await _audit.RecordAsync(AuditCategory, "dns.upsert", domain, $"proxied CNAME → {target}",
                        success: false, error: ex.Message, ct: ct);
                    await SetRouteStatusAsync(routeIds, RouteStatus.Error, $"DNS record not written: {ex.Message}", ct);
                    _logger.LogWarning(ex, "Failed to upsert the CNAME for {Domain}.", domain);
                }
            }

            // Protected routes get a Zero Trust Access application in front of their hostname — which,
            // since new routes are protected by default (ADR-0035), is most of them.
            await ReconcileAccessAppsAsync(cf, routes, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to apply the Cloudflare tunnel configuration; will be retried on the next change.");
        }
    }

    /// <inheritdoc />
    public async Task ConnectStackAsync(int stackId, CancellationToken ct = default) {
        if (!Enabled) return;
        var container = IngressMemberContainer(_options.CurrentValue.Proxy.Cloudflare);
        if (container is null) return; // Fully unmanaged: the operator owns connectivity.
        try {
            await _networks.ConnectStackServicesAsync(stackId, container, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to connect stack {StackId} services to its ingress network.", stackId);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsRunningAsync(CancellationToken ct = default) {
        if (!Enabled) return false;
        var container = IngressMemberContainer(_options.CurrentValue.Proxy.Cloudflare);
        if (container is null) return true; // Fully unmanaged: nothing observable locally — report configured.
        try {
            var details = await _docker.InspectContainerAsync(container, ct);
            return details.State?.Status == "running";
        } catch {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ForgetDomainAsync(string domain, string? actor, CancellationToken ct = default) {
        if (!Enabled) return;
        var cf = _options.CurrentValue.Proxy.Cloudflare;
        if (Misconfigured(cf, out var why))
            throw new InvalidOperationException($"Cloudflare tunnel provider is not configured: {why}");
        var tunnel = await _api.FindTunnelAsync(cf.AccountId!, cf.TunnelName, cf.ApiToken!, ct);
        if (tunnel is null) return; // Nothing of Watchtower's to remove.

        // 1. The ingress rule(s) for the hostname, from the configured tunnel only.
        var existing = await _api.GetTunnelConfigurationAsync(cf.AccountId!, tunnel.Id, cf.ApiToken!, ct);
        var remaining = WithoutHostname(existing, domain);
        if (remaining.Count != existing.Count) {
            try {
                await _api.PutTunnelConfigurationAsync(cf.AccountId!, tunnel.Id, remaining, cf.ApiToken!, ct);
                await _audit.RecordAsync(AuditCategory, "tunnel.rule.remove", domain,
                    $"{existing.Count - remaining.Count} ingress rule(s) removed from {tunnel.Name}", actor: actor, ct: ct);
            } catch (Exception ex) {
                await _audit.RecordAsync(AuditCategory, "tunnel.rule.remove", domain, $"from {tunnel.Name}",
                    success: false, error: ex.Message, actor: actor, ct: ct);
                throw;
            }
        }

        // 2. The CNAME — but only one that points at THIS tunnel. A record someone else made for the
        // name is not Watchtower's to delete, even though the route named the same hostname.
        var target = $"{tunnel.Id}.cfargotunnel.com";
        var records = await _api.ListDnsRecordsAsync(cf.ZoneId!, domain, cf.ApiToken!, ct);
        foreach (var record in records.Where(r =>
                     string.Equals(r.Type, "CNAME", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(r.Content, target, StringComparison.OrdinalIgnoreCase))) {
            try {
                await _api.DeleteDnsRecordAsync(cf.ZoneId!, record.Id, cf.ApiToken!, ct);
                await _audit.RecordAsync(AuditCategory, "dns.delete", domain, $"proxied CNAME → {target}", actor: actor, ct: ct);
            } catch (Exception ex) {
                await _audit.RecordAsync(AuditCategory, "dns.delete", domain, $"proxied CNAME → {target}",
                    success: false, error: ex.Message, actor: actor, ct: ct);
                throw;
            }
        }
        _logger.LogInformation("Forgot {Domain}: its ingress rule and tunnel CNAME were removed.", domain);
    }

    /// <summary>Every rule except those for <paramref name="hostname"/>; the catch-all (no hostname) always stays.</summary>
    internal static List<CloudflareIngressRule> WithoutHostname(IReadOnlyList<CloudflareIngressRule> rules, string hostname) =>
        rules.Where(r => !string.Equals(r.Hostname, hostname, StringComparison.OrdinalIgnoreCase)).ToList();

    // ── Reconcile ─────────────────────────────────────────────────────────────

    private async Task ReconcileAsync(CancellationToken ct) {
        try {
            var cf = _options.CurrentValue.Proxy.Cloudflare;
            if (Misconfigured(cf, out var why)) {
                _logger.LogWarning("Cloudflare tunnel provider is selected but not reconciled: {Reason}", why);
                return;
            }
            var tunnel = await EnsureTunnelAsync(cf, ct);
            if (tunnel is null) return;
            if (cf.Managed)
                await EnsureCloudflaredContainerAsync(cf, tunnel, ct);
            else
                await RemoveStaleManagedCloudflaredAsync(ct);
            var member = IngressMemberContainer(cf);
            if (member is not null)
                await _networks.ConnectAllRoutedContainersAsync(member, ct);
            await ApplyAsync(ct);
            _logger.LogInformation("Cloudflare tunnel reconciled.");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Shutting down.
        } catch (Exception ex) {
            _logger.LogError(ex, "Cloudflare tunnel reconcile failed; will be retried on the next route change or deploy.");
        }
    }

    /// <summary>
    /// Runtime disable / provider switch: removes the managed cloudflared container. The tunnel and the
    /// DNS records are deliberately kept — deleting public DNS the operator may still want is not this
    /// toggle's job, and re-enabling later reuses both. Unmanaged containers are never touched.
    /// </summary>
    private async Task TeardownAsync(CancellationToken ct) {
        if (!_applied.Cloudflare.Managed) {
            _logger.LogInformation("Cloudflare provider deactivated; the operator-run cloudflared is left as is.");
            return;
        }
        try {
            await _docker.StopContainerAsync(CloudflaredContainerName, ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Stopping the cloudflared container failed (it may not exist).");
        }
        try {
            await _docker.RemoveContainerAsync(CloudflaredContainerName, ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Removing the cloudflared container failed (it may not exist).");
        }
        _logger.LogInformation(
            "Cloudflare provider deactivated: the managed cloudflared container was stopped and removed. " +
            "The tunnel and DNS records are kept for re-enabling.");
    }

    private async Task<CloudflareTunnel?> EnsureTunnelAsync(CloudflareProxyOptions cf, CancellationToken ct) {
        var tunnel = await _api.FindTunnelAsync(cf.AccountId!, cf.TunnelName, cf.ApiToken!, ct);
        if (tunnel is not null) return tunnel;
        if (!cf.Managed) {
            // Unmanaged: the operator created the tunnel when they set up cloudflared; a missing one
            // means the name is wrong, and creating a second tunnel would only hide that.
            _logger.LogWarning(
                "Tunnel '{Tunnel}' not found in the account. In unmanaged mode Watchtower does not create " +
                "tunnels — check Proxy:Cloudflare:TunnelName against your cloudflared setup.", cf.TunnelName);
            return null;
        }
        _logger.LogInformation("Creating Cloudflare tunnel '{Tunnel}'.", cf.TunnelName);
        try {
            var created = await _api.CreateTunnelAsync(cf.AccountId!, cf.TunnelName, cf.ApiToken!, ct);
            await _audit.RecordAsync(AuditCategory, "tunnel.create", cf.TunnelName, "remotely-managed tunnel", ct: ct);
            return created;
        } catch (Exception ex) {
            await _audit.RecordAsync(AuditCategory, "tunnel.create", cf.TunnelName, "remotely-managed tunnel",
                success: false, error: ex.Message, ct: ct);
            throw;
        }
    }

    /// <summary>
    /// Unmanaged mode must not leave a previously-managed cloudflared behind: flipping the switch
    /// off while the provider stays enabled is a Refresh transition, and without this the container
    /// created under managed mode would keep serving its tunnel forever (restart policy
    /// unless-stopped). Only a container carrying Watchtower's managed label is removed — an
    /// operator-run container that happens to share the name is never touched.
    /// </summary>
    private async Task RemoveStaleManagedCloudflaredAsync(CancellationToken ct) {
        DockerContainerDetails details;
        try {
            details = await _docker.InspectContainerAsync(CloudflaredContainerName, ct);
        } catch {
            return; // Not present — nothing to converge.
        }
        if (details.Config.Labels is not { } labels || !labels.ContainsKey(ManagedLabelKey)) {
            _logger.LogWarning(
                "A container named {Name} exists but does not carry Watchtower's managed label — leaving it alone.",
                CloudflaredContainerName);
            return;
        }
        try {
            await _docker.StopContainerAsync(CloudflaredContainerName, ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Stopping the stale managed cloudflared failed (it may already be stopped).");
        }
        await _docker.RemoveContainerAsync(CloudflaredContainerName, ct);
        _logger.LogInformation(
            "Removed the managed cloudflared container — the managed switch is off. The tunnel it served is kept.");
    }

    private async Task EnsureCloudflaredContainerAsync(
        CloudflareProxyOptions cf, CloudflareTunnel tunnel, CancellationToken ct) {
        // Reuse a healthy container; otherwise remove any stale one and recreate with a fresh token.
        try {
            var details = await _docker.InspectContainerAsync(CloudflaredContainerName, ct);
            if (details.State?.Status == "running") {
                _logger.LogInformation("cloudflared container already running; reusing it.");
                return;
            }
            _logger.LogInformation("Removing stale cloudflared container (status {Status})", details.State?.Status);
            await _docker.RemoveContainerAsync(CloudflaredContainerName, ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "No existing cloudflared container found; creating a new one.");
        }

        var token = await _api.GetTunnelTokenAsync(cf.AccountId!, tunnel.Id, cf.ApiToken!, ct);
        _logger.LogInformation("Pulling {Image}", cf.CloudflaredImage);
        await _docker.PullImageAsync(cf.CloudflaredImage, ct: ct);

        var body = new DockerCreateContainerBody {
            Image = cf.CloudflaredImage,
            Cmd = ["tunnel", "--no-autoupdate", "run", "--token", token],
            Labels = new Dictionary<string, string> { [ManagedLabelKey] = "cloudflared" },
            HostConfig = new DockerCreateHostConfig {
                RestartPolicy = new DockerRestartPolicy { Name = "unless-stopped" },
            },
        };
        var id = await _docker.CreateContainerAsync(body, CloudflaredContainerName, ct);
        // cloudflared joins each stack's ingress network on demand (ConnectAllRoutedContainersAsync /
        // ConnectStackAsync); outbound tunnel traffic needs no special network.
        await _docker.StartContainerAsync(id, ct);
        _logger.LogInformation("Started managed cloudflared container {ShortId}", id.Length >= 12 ? id[..12] : id);
    }

    /// <summary>
    /// The container that must sit on the ingress networks for the generated service URLs to resolve:
    /// the managed cloudflared, or the operator's named container in unmanaged mode (null when they
    /// run cloudflared elsewhere and own connectivity themselves).
    /// </summary>
    private static string? IngressMemberContainer(CloudflareProxyOptions cf) =>
        cf.Managed ? CloudflaredContainerName
        : string.IsNullOrWhiteSpace(cf.CloudflaredContainerName) ? null
        : cf.CloudflaredContainerName.Trim();

    private static bool Misconfigured(CloudflareProxyOptions cf, out string reason) {
        if (string.IsNullOrWhiteSpace(cf.AccountId)) { reason = "Proxy:Cloudflare:AccountId is not set."; return true; }
        if (string.IsNullOrWhiteSpace(cf.ZoneId)) { reason = "Proxy:Cloudflare:ZoneId is not set."; return true; }
        if (string.IsNullOrWhiteSpace(cf.ApiToken)) { reason = "Proxy:Cloudflare:ApiToken is not set."; return true; }
        if (string.IsNullOrWhiteSpace(cf.TunnelName)) { reason = "Proxy:Cloudflare:TunnelName is not set."; return true; }
        reason = "";
        return false;
    }

    private async Task<IReadOnlyList<Route>> LoadRoutesAsync(CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Routes.AsNoTracking().Include(r => r.Stack).ToListAsync(ct);
    }

    /// <summary>
    /// Writes the reconcile outcome onto the route rows. Best-effort and bounded: the status is a
    /// convenience for the Routes page, the audit trail is the record — so a bookkeeping failure is
    /// logged, never allowed to fail the reconcile.
    /// </summary>
    private async Task SetRouteStatusAsync(IEnumerable<int> routeIds, RouteStatus status, string? detail, CancellationToken ct) {
        var ids = routeIds.ToList();
        if (ids.Count == 0) return;
        var capped = detail is { Length: > 500 } ? detail[..500] : detail;
        try {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.Routes
                .Where(r => ids.Contains(r.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.StatusDetail, capped), ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogDebug(ex, "Failed to record the route status for {Count} route(s).", ids.Count);
        }
    }

    // ── Zero Trust Access applications ───────────────────────────────────────

    private const string AccessAppNamePrefix = "watchtower: ";
    private const string AccessPolicyName = "watchtower";

    /// <summary>The suffix that tells a route's bypass application apart from the route's own.</summary>
    private const string AccessBypassNameSuffix = " (public paths)";

    /// <summary>The policy decision that admits the app's allow-list and nobody else.</summary>
    internal const string AccessDecisionAllow = "allow";

    /// <summary>The policy decision that admits nobody — a protected route with no allow source.</summary>
    internal const string AccessDecisionDeny = "deny";

    /// <summary>The policy decision that lets everyone through without signing in — the bypass paths.</summary>
    internal const string AccessDecisionBypass = "bypass";

    /// <summary>
    /// One desired Access application: the hostname it covers, what its Watchtower-owned policy decides,
    /// and — for an <see cref="AccessDecisionAllow"/> one — who that policy admits.
    /// </summary>
    /// <param name="RouteId">
    /// The route this application was projected from, so a lockout can be written back onto the row that
    /// caused it. Not part of the app's identity at the edge: two applications can carry the same id.
    /// </param>
    /// <param name="Destinations">
    /// Every <c>host</c>/<c>host/path</c> the application covers when one hostname is not enough — a
    /// bypass app naming each of the route's public paths. Null for the ordinary whole-hostname app.
    /// </param>
    internal sealed record AccessAppSpec(
        int RouteId,
        string Domain,
        string Name,
        string[] Emails,
        string[] EmailDomains,
        string[] GroupIds,
        string[] ReusablePolicyIds,
        string Decision = AccessDecisionAllow,
        string[]? Destinations = null) {
        /// <summary>Whether a Watchtower-generated app-scoped policy is needed (any inline rule at all).</summary>
        public bool HasInlineRules => Emails.Length > 0 || EmailDomains.Length > 0 || GroupIds.Length > 0;

        /// <summary>
        /// Whether this application denies everyone — the answer to a protected route whose allow-list is
        /// empty. The route it came from is marked <see cref="RouteStatus.Error"/>, because a hostname
        /// nobody can reach is a misconfiguration rather than a policy.
        /// </summary>
        public bool IsLockout => string.Equals(Decision, AccessDecisionDeny, StringComparison.Ordinal);
    }

    /// <summary>The desired Access apps plus the warnings for routes that could not be projected.</summary>
    internal sealed record AccessProjection(List<AccessAppSpec> Apps, List<string> Warnings);

    /// <summary>
    /// Projects the protected routes onto Access applications — pure, for tests.
    /// <see cref="AccessMode.Authenticated"/> admits the instance-wide configured allow sources: emails,
    /// email domains, Access group ids (the "main user group" workflow — e.g. an existing group of Entra
    /// ID users), and/or reusable policy ids attached to the app.
    /// <see cref="AccessMode.Restricted"/> admits exactly the emails behind the route's grants (granted
    /// users plus granted groups' members — accounts without an email cannot be matched by Cloudflare
    /// and are effectively excluded).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A protected route with no allow source at all gets a <see cref="AccessDecisionDeny"/> application
    /// (ADR-0035) rather than being skipped. The rule used to run the other way — no app, a warning, and
    /// whatever was already at the edge left alone — on the reasoning that a silent total lockout is worse
    /// than an open door. It is not: since new routes are protected by default, the skip is what would be
    /// silent, publishing an unauthenticated hostname the operator believes is gated. A lockout announces
    /// itself, in the warning, in the audit trail and as <see cref="RouteStatus.Error"/> on the route.
    /// </para>
    /// <para>
    /// A protected route with bypass paths gets a second, <see cref="AccessDecisionBypass"/> application
    /// covering exactly those paths — a deny route included, because a public webhook has to keep working
    /// while the operator sorts the allow list out. Cloudflare applies the most specific application, so
    /// the bypass one wins for the paths it names. Its match is path-segment based and therefore wider
    /// than Watchtower's own raw-prefix <see cref="RouteAccessPolicy.IsExemptPath"/>; a Public route gets
    /// none at all, having no access control for anything to be excepted from.
    /// </para>
    /// </remarks>
    internal static AccessProjection ProjectAccessApps(
        IReadOnlyList<Route> routes,
        IReadOnlyDictionary<int, string[]> grantedEmailsByRouteId,
        CloudflareProxyOptions cf) {
        var apps = new List<AccessAppSpec>();
        var warnings = new List<string>();
        foreach (var route in routes.Where(r => r.AccessMode != AccessMode.Public).OrderBy(r => r.Domain, StringComparer.Ordinal)) {
            // An Access application is attached to a hostname, so a route without one cannot have one.
            // Unreachable in practice — ck_routes_binding stores a port route as Public — and skipped
            // rather than thrown about, because this is a projection.
            if (route.Domain is not { } domain) continue;
            string[] emails;
            string[] emailDomains;
            string[] groupIds;
            string[] reusablePolicyIds;
            if (route.AccessMode == AccessMode.Authenticated) {
                emails = CloudflareProxyOptions.SplitList(cf.AccessAllowedEmails);
                emailDomains = CloudflareProxyOptions.SplitList(cf.AccessAllowedEmailDomains);
                groupIds = CloudflareProxyOptions.SplitList(cf.AccessGroupIds);
                reusablePolicyIds = CloudflareProxyOptions.SplitList(cf.AccessReusablePolicyIds);
            } else {
                // Restricted means "only these subjects" — the instance-wide sources must not widen it.
                emails = grantedEmailsByRouteId.TryGetValue(route.Id, out var granted) ? granted : [];
                emailDomains = [];
                groupIds = [];
                reusablePolicyIds = [];
            }
            var lockedOut = emails.Length == 0 && emailDomains.Length == 0
                && groupIds.Length == 0 && reusablePolicyIds.Length == 0;
            if (lockedOut) {
                warnings.Add(
                    $"Route {domain} is {route.AccessMode} but nobody could pass its Access policy — " +
                    (route.AccessMode == AccessMode.Authenticated
                        ? "configure allowed emails, email domains, an Access group id or a reusable policy id in the proxy settings. "
                        : "grant users (or groups with members) that have an email address. ") +
                    "Access is denying everyone until then.");
            }
            apps.Add(new AccessAppSpec(
                route.Id,
                domain,
                AccessAppNamePrefix + domain,
                emails.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToArray(),
                emailDomains.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray(),
                groupIds.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToArray(),
                reusablePolicyIds.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
                Decision: lockedOut ? AccessDecisionDeny : AccessDecisionAllow));

            // The trailing slash goes because Cloudflare matches path segments, so `/webhooks` and
            // `/webhooks/` name the same destination and would otherwise churn as two. An entry that is
            // nothing but slashes would name the hostname itself and collide with the app above, so it is
            // dropped: bypassing a whole protected route is not something to infer from a stray line.
            var bypassPaths = RouteAccessPolicy.ParseBypassPaths(route.BypassPaths)
                .Select(path => path.TrimEnd('/'))
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (bypassPaths.Length == 0) continue;
            apps.Add(new AccessAppSpec(
                route.Id,
                // The primary hostname of an application is one value, so it is the first path; the rest
                // ride along as destinations. Which one is primary decides nothing but what the dashboard
                // shows.
                domain + bypassPaths[0],
                AccessAppNamePrefix + domain + AccessBypassNameSuffix,
                [], [], [], [],
                Decision: AccessDecisionBypass,
                Destinations: [.. bypassPaths.Select(path => domain + path)]));
        }
        return new AccessProjection(apps, warnings);
    }

    /// <summary>
    /// The Watchtower-created Access applications the projection no longer wants — pure, for tests.
    /// </summary>
    /// <remarks>
    /// Keyed on the desired domains <em>and</em> the desired names, because the two identities do not
    /// always agree. A bypass application's domain carries a path, so a rename or an added public path
    /// changes it while the name stays put; matching on names alone would in turn delete an app whose
    /// hostname is still wanted under a name Cloudflare normalised differently. Wanting either is enough
    /// to keep it — otherwise every reconcile would delete the bypass app and create it again.
    /// Only apps carrying the <see cref="AccessAppNamePrefix"/> are ever candidates: an app somebody made
    /// in the dashboard is not ours to remove.
    /// </remarks>
    internal static IEnumerable<CloudflareAccessApp> StaleApps(
        IReadOnlyList<CloudflareAccessApp> existing, AccessProjection projection) {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(projection);
        var wantedDomains = projection.Apps.Select(a => a.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wantedNames = projection.Apps.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        return existing.Where(app =>
            app.Name.StartsWith(AccessAppNamePrefix, StringComparison.Ordinal)
            && !wantedDomains.Contains(app.Domain)
            && !wantedNames.Contains(app.Name));
    }

    /// <summary>
    /// Emails admitted by each restricted route's grants: directly granted users plus every member of
    /// each granted group — enabled accounts with an email only.
    /// </summary>
    private async Task<Dictionary<int, string[]>> LoadGrantedEmailsAsync(
        IReadOnlyList<int> routeIds, CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var direct = await db.RouteAccessGrants.AsNoTracking()
            .Where(g => g.UserId != null && routeIds.Contains(g.RouteId))
            .Select(g => new { g.RouteId, g.User!.Email, g.User.Disabled })
            .ToListAsync(ct);
        var viaGroups = await db.RouteAccessGrants.AsNoTracking()
            .Where(g => g.GroupId != null && routeIds.Contains(g.RouteId))
            .SelectMany(g => db.GroupMembers
                .Where(m => m.GroupId == g.GroupId)
                .Select(m => new { g.RouteId, m.User!.Email, m.User.Disabled }))
            .ToListAsync(ct);
        return direct.Concat(viaGroups)
            .Where(x => !x.Disabled && !string.IsNullOrWhiteSpace(x.Email))
            .GroupBy(x => x.RouteId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Email!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>
    /// Makes the account's Access applications match the protected routes: create/update one
    /// <c>self_hosted</c> app per protected hostname with a single Watchtower-owned policy — allow, or
    /// deny when nothing could pass it — plus a bypass app for each route's public paths, and delete the
    /// Watchtower-created apps the projection no longer wants (<see cref="StaleApps"/>). Only apps
    /// carrying the <see cref="AccessAppNamePrefix"/> are ever deleted — dashboard-made apps are never
    /// touched. Best-effort per app; a token without <c>Access: Apps and Policies:Edit</c> logs one
    /// warning.
    /// </summary>
    private async Task ReconcileAccessAppsAsync(
        CloudflareProxyOptions cf, IReadOnlyList<Route> routes, CancellationToken ct) {
        var restrictedIds = routes.Where(r => r.AccessMode == AccessMode.Restricted).Select(r => r.Id).ToList();
        var granted = restrictedIds.Count > 0
            ? await LoadGrantedEmailsAsync(restrictedIds, ct)
            : new Dictionary<int, string[]>();
        var projection = ProjectAccessApps(routes, granted, cf);
        foreach (var warning in projection.Warnings)
            _logger.LogWarning("{Warning}", warning);

        // Listed even when nothing is protected: a route flipped back to Public still needs its
        // Watchtower-created app removed below.
        IReadOnlyList<CloudflareAccessApp> existing;
        try {
            existing = await _api.ListAccessAppsAsync(cf.AccountId!, cf.ApiToken!, ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex,
                "Could not list Access applications — protected routes are not gated at the edge. " +
                "The API token may lack the 'Access: Apps and Policies: Edit' permission.");
            return;
        }

        foreach (var spec in projection.Apps) {
            try {
                var request = new CloudflareAccessAppRequest {
                    Name = spec.Name,
                    Domain = spec.Domain,
                    Type = "self_hosted",
                    SessionDuration = "24h",
                    AppLauncherVisible = false,
                    // Reusable policies (the dashboard-maintained "default policy" workflow) attach on
                    // the app itself; null leaves any existing attachments alone when none are configured.
                    // Never on a deny or bypass app: attaching an allow-list to one would undo the very
                    // decision it exists to make.
                    Policies = spec.Decision == AccessDecisionAllow && spec.ReusablePolicyIds.Length > 0
                        ? spec.ReusablePolicyIds
                        : null,
                    Destinations = spec.Destinations is { Length: > 0 } uris
                        ? [.. uris.Select(CloudflareAccessDestination.Public)]
                        : null,
                };
                // By domain or by name: a bypass app's domain carries a path, so a public path added or
                // removed moves it while the name — the identity Watchtower gave it — stays. Matching on
                // one of the two would create a duplicate app rather than update the one that is there.
                var found = existing.FirstOrDefault(a =>
                    string.Equals(a.Domain, spec.Domain, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Name, spec.Name, StringComparison.Ordinal));
                var app = found is null
                    ? await _api.CreateAccessAppAsync(cf.AccountId!, request, cf.ApiToken!, ct)
                    : await _api.UpdateAccessAppAsync(cf.AccountId!, found.Id, request, cf.ApiToken!, ct);

                // The Watchtower-generated app-scoped policy carries the inline rules (emails, email
                // domains, Access groups). When only reusable policies are configured it is removed
                // rather than left stale — an orphaned allow-list would keep admitting old members.
                var policies = await _api.ListAccessPoliciesAsync(cf.AccountId!, app.Id, cf.ApiToken!, ct);
                var mine = policies.FirstOrDefault(p => string.Equals(p.Name, AccessPolicyName, StringComparison.Ordinal));
                var ruleCount = 0;
                // A deny or bypass policy is about everyone, so it is written whether or not the spec
                // carries inline rules — that emptiness is precisely what a deny app is answering.
                if (spec.Decision != AccessDecisionAllow || spec.HasInlineRules) {
                    var include = spec.Decision == AccessDecisionAllow
                        ? [
                            .. spec.Emails.Select(CloudflareAccessRule.ForEmail),
                            .. spec.EmailDomains.Select(CloudflareAccessRule.ForEmailDomain),
                            .. spec.GroupIds.Select(CloudflareAccessRule.ForGroup),
                        ]
                        : new[] { CloudflareAccessRule.ForEveryone() };
                    ruleCount = include.Length;
                    var policyRequest = new CloudflareAccessPolicyRequest {
                        Name = AccessPolicyName, Decision = spec.Decision, Include = include, Precedence = 1,
                    };
                    if (mine is null)
                        await _api.CreateAccessPolicyAsync(cf.AccountId!, app.Id, policyRequest, cf.ApiToken!, ct);
                    else
                        await _api.UpdateAccessPolicyAsync(cf.AccountId!, app.Id, mine.Id, policyRequest, cf.ApiToken!, ct);
                } else if (mine is not null) {
                    await _api.DeleteAccessPolicyAsync(cf.AccountId!, app.Id, mine.Id, cf.ApiToken!, ct);
                }
                // The decision leads the detail line: "allow" and "deny" on the same hostname are the
                // difference between a gate and a wall, and the trail is where an operator finds out which
                // one a reconcile settled on.
                await _audit.RecordAsync(AuditCategory,
                    found is null ? "access.app.create" : "access.app.sync",
                    spec.Domain,
                    $"{spec.Decision} · {ruleCount} inline rule(s), "
                    + $"{spec.ReusablePolicyIds.Length} reusable policy(ies)", ct: ct);
                _logger.LogInformation(
                    "Access application reconciled for {Domain} ({Decision}, {Rules} inline rule(s), {Reusable} reusable policy(ies)).",
                    spec.Domain, spec.Decision, ruleCount, spec.ReusablePolicyIds.Length);
            } catch (Exception ex) {
                await _audit.RecordAsync(AuditCategory, "access.app.sync", spec.Domain, detail: null,
                    success: false, error: ex.Message, ct: ct);
                _logger.LogWarning(ex, "Failed to reconcile the Access application for {Domain}.", spec.Domain);
            }
        }

        // Every route the projection had to lock out, marked on the row so the Routes page says so rather
        // than reading healthy while the edge answers 403 to everybody. After the loop, because it is the
        // projection that decided this, not whether any single API call went through.
        await SetRouteStatusAsync(
            projection.Apps.Where(a => a.IsLockout).Select(a => a.RouteId).Distinct(),
            RouteStatus.Error,
            "Cloudflare Access is denying everyone: no allow source is configured. Add allowed emails, "
            + "email domains, an Access group id or a reusable policy id under Settings → Reverse proxy, "
            + "or set this route to public.",
            ct);

        foreach (var stale in StaleApps(existing, projection)) {
            try {
                await _api.DeleteAccessAppAsync(cf.AccountId!, stale.Id, cf.ApiToken!, ct);
                await _audit.RecordAsync(AuditCategory, "access.app.delete", stale.Domain,
                    "no longer projected", ct: ct);
                _logger.LogInformation("Removed the Access application for {Domain} (no longer projected).", stale.Domain);
            } catch (Exception ex) {
                await _audit.RecordAsync(AuditCategory, "access.app.delete", stale.Domain,
                    "no longer projected", success: false, error: ex.Message, ct: ct);
                _logger.LogWarning(ex, "Failed to remove the stale Access application for {Domain}.", stale.Domain);
            }
        }
    }

    /// <summary>
    /// Projects the route table onto tunnel ingress rules — pure, for tests. One exact-hostname rule
    /// per route (<c>http://{project}-{service}:{port}</c>, plain HTTP inside the private ingress
    /// network; TLS is Cloudflare's edge job), sorted by hostname for a stable configuration, and the
    /// mandatory catch-all 404 last so unknown hostnames don't leak an arbitrary upstream.
    /// </summary>
    /// <remarks>
    /// A <see cref="RouteTarget.Watchtower"/> route has no stack and is filtered out here as well as by the
    /// caller — the projection is the pure function the tests drive directly, so it states the rule itself.
    /// </remarks>
    internal static List<CloudflareIngressRule> ProjectIngress(IReadOnlyList<Route> routes) {
        var rules = routes
            .Where(r => r.Target == RouteTarget.Service && r.Stack is not null)
            .Select(r => new CloudflareIngressRule {
                Hostname = r.Domain,
                Service = $"http://{ProxyIngressNetworks.EdgeAlias(r.Stack!.ComposeProjectName, r.ServiceName)}:{r.ContainerPort}",
            })
            .OrderBy(r => r.Hostname, StringComparer.Ordinal)
            .ToList();
        rules.Add(new CloudflareIngressRule { Service = "http_status:404" });
        return rules;
    }

    /// <summary>
    /// The dashboard-made rules of a tunnel configuration: hostname rules whose hostname is not in
    /// Watchtower's route table. Catch-alls are never foreign (the projection always writes its own),
    /// and a foreign rule for a hostname the table DOES know is not foreign either — the route row is
    /// the operator's newer statement about that hostname, so the projection's rule wins.
    /// </summary>
    internal static List<CloudflareIngressRule> ForeignIngressRules(
        IReadOnlyList<CloudflareIngressRule> existing, IEnumerable<string> routeDomains) {
        var owned = new HashSet<string>(routeDomains, StringComparer.OrdinalIgnoreCase);
        return existing
            .Where(r => !string.IsNullOrWhiteSpace(r.Hostname) && !owned.Contains(r.Hostname!))
            .ToList();
    }

    /// <summary>
    /// Merges the projection with a tunnel's current configuration — pure, for tests. Foreign rules
    /// (see <see cref="ForeignIngressRules"/>) come first, verbatim and in their original order (order
    /// matters for path-narrowed rules); then Watchtower's hostname rules; then the single catch-all.
    /// This is what makes pointing Watchtower at a pre-existing tunnel non-destructive: the whole-config
    /// PUT round-trips everything it does not own.
    /// </summary>
    internal static List<CloudflareIngressRule> MergeIngress(
        IReadOnlyList<CloudflareIngressRule> existing,
        IReadOnlyList<CloudflareIngressRule> projected,
        IEnumerable<string> routeDomains) {
        var merged = ForeignIngressRules(existing, routeDomains);
        merged.AddRange(projected);
        return merged;
    }
}
