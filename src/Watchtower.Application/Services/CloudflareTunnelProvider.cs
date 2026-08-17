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

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DockerEngineClient _docker;
    private readonly ProxyIngressNetworks _networks;
    private readonly CloudflareApiClient _api;
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
        IOptionsMonitor<WatchtowerOptions> options,
        ILogger<CloudflareTunnelProvider> logger) {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _networks = networks;
        _api = api;
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

            var routes = await LoadRoutesAsync(ct);
            var ingress = ProjectIngress(routes);
            await _api.PutTunnelConfigurationAsync(cf.AccountId!, tunnel.Id, ingress, cf.ApiToken!, ct);
            _logger.LogInformation("Pushed {Count} ingress rule(s) to tunnel {Tunnel}.", ingress.Count - 1, tunnel.Name);

            var target = $"{tunnel.Id}.cfargotunnel.com";
            foreach (var domain in routes.Select(r => r.Domain).Distinct(StringComparer.OrdinalIgnoreCase)) {
                try {
                    await _api.UpsertDnsCnameAsync(cf.ZoneId!, domain, target, cf.ApiToken!, ct);
                } catch (Exception ex) {
                    // Per-domain best effort: one domain outside the configured zone must not stop the rest.
                    _logger.LogWarning(ex, "Failed to upsert the CNAME for {Domain}.", domain);
                }
            }

            // Phase 3: protected routes get a Zero Trust Access application in front of their hostname.
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
        return await _api.CreateTunnelAsync(cf.AccountId!, cf.TunnelName, cf.ApiToken!, ct);
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

    // ── Zero Trust Access applications (phase 3) ─────────────────────────────

    private const string AccessAppNamePrefix = "watchtower: ";
    private const string AccessPolicyName = "watchtower";

    /// <summary>One desired Access application: the hostname and who its allow policy admits.</summary>
    internal sealed record AccessAppSpec(
        string Domain,
        string Name,
        string[] Emails,
        string[] EmailDomains,
        string[] GroupIds,
        string[] ReusablePolicyIds) {
        /// <summary>Whether a Watchtower-generated app-scoped policy is needed (any inline rule at all).</summary>
        public bool HasInlineRules => Emails.Length > 0 || EmailDomains.Length > 0 || GroupIds.Length > 0;
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
    /// and are effectively excluded). A protected route with no allow source at all is skipped with a
    /// warning rather than published as a deny-all app: a silent total lockout right when the operator
    /// flips a switch is the worse failure, and the skip keeps any pre-existing app untouched.
    /// </summary>
    internal static AccessProjection ProjectAccessApps(
        IReadOnlyList<Route> routes,
        IReadOnlyDictionary<int, string[]> grantedEmailsByRouteId,
        CloudflareProxyOptions cf) {
        var apps = new List<AccessAppSpec>();
        var warnings = new List<string>();
        foreach (var route in routes.Where(r => r.AccessMode != AccessMode.Public).OrderBy(r => r.Domain, StringComparer.Ordinal)) {
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
            if (emails.Length == 0 && emailDomains.Length == 0 && groupIds.Length == 0 && reusablePolicyIds.Length == 0) {
                warnings.Add(
                    $"Route {route.Domain} is {route.AccessMode} but nobody could pass its Access policy — " +
                    (route.AccessMode == AccessMode.Authenticated
                        ? "configure allowed emails, email domains, an Access group id or a reusable policy id in the proxy settings. "
                        : "grant users (or groups with members) that have an email address. ") +
                    "The Access application was not created/updated.");
                continue;
            }
            apps.Add(new AccessAppSpec(
                route.Domain,
                AccessAppNamePrefix + route.Domain,
                emails.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToArray(),
                emailDomains.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray(),
                groupIds.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToArray(),
                reusablePolicyIds.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        return new AccessProjection(apps, warnings);
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
    /// <c>self_hosted</c> app per protected hostname with a single Watchtower-owned allow policy, and
    /// delete Watchtower-created apps whose hostname is no longer protected. Only apps carrying the
    /// <see cref="AccessAppNamePrefix"/> are ever deleted — dashboard-made apps are never touched.
    /// Best-effort per app; a token without <c>Access: Apps and Policies:Edit</c> logs one warning.
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
                    Policies = spec.ReusablePolicyIds.Length > 0 ? spec.ReusablePolicyIds : null,
                };
                var app = existing.FirstOrDefault(a => string.Equals(a.Domain, spec.Domain, StringComparison.OrdinalIgnoreCase));
                app = app is null
                    ? await _api.CreateAccessAppAsync(cf.AccountId!, request, cf.ApiToken!, ct)
                    : await _api.UpdateAccessAppAsync(cf.AccountId!, app.Id, request, cf.ApiToken!, ct);

                // The Watchtower-generated app-scoped policy carries the inline rules (emails, email
                // domains, Access groups). When only reusable policies are configured it is removed
                // rather than left stale — an orphaned allow-list would keep admitting old members.
                var policies = await _api.ListAccessPoliciesAsync(cf.AccountId!, app.Id, cf.ApiToken!, ct);
                var mine = policies.FirstOrDefault(p => string.Equals(p.Name, AccessPolicyName, StringComparison.Ordinal));
                var ruleCount = 0;
                if (spec.HasInlineRules) {
                    var include = spec.Emails.Select(CloudflareAccessRule.ForEmail)
                        .Concat(spec.EmailDomains.Select(CloudflareAccessRule.ForEmailDomain))
                        .Concat(spec.GroupIds.Select(CloudflareAccessRule.ForGroup))
                        .ToArray();
                    ruleCount = include.Length;
                    var policyRequest = new CloudflareAccessPolicyRequest {
                        Name = AccessPolicyName, Decision = "allow", Include = include, Precedence = 1,
                    };
                    if (mine is null)
                        await _api.CreateAccessPolicyAsync(cf.AccountId!, app.Id, policyRequest, cf.ApiToken!, ct);
                    else
                        await _api.UpdateAccessPolicyAsync(cf.AccountId!, app.Id, mine.Id, policyRequest, cf.ApiToken!, ct);
                } else if (mine is not null) {
                    await _api.DeleteAccessPolicyAsync(cf.AccountId!, app.Id, mine.Id, cf.ApiToken!, ct);
                }
                _logger.LogInformation(
                    "Access application reconciled for {Domain} ({Rules} inline rule(s), {Reusable} reusable policy(ies)).",
                    spec.Domain, ruleCount, spec.ReusablePolicyIds.Length);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to reconcile the Access application for {Domain}.", spec.Domain);
            }
        }

        // Deletion set: Watchtower-created apps whose hostname is no longer protected AT ALL. A protected
        // route that was merely skipped (empty allow-list) keeps its existing app untouched — deleting it
        // would silently un-gate a route the operator marked protected.
        var protectedDomains = routes
            .Where(r => r.AccessMode != AccessMode.Public)
            .Select(r => r.Domain)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in existing.Where(a =>
                     a.Name.StartsWith(AccessAppNamePrefix, StringComparison.Ordinal)
                     && !protectedDomains.Contains(a.Domain))) {
            try {
                await _api.DeleteAccessAppAsync(cf.AccountId!, stale.Id, cf.ApiToken!, ct);
                _logger.LogInformation("Removed the Access application for {Domain} (route no longer protected).", stale.Domain);
            } catch (Exception ex) {
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
    internal static List<CloudflareIngressRule> ProjectIngress(IReadOnlyList<Route> routes) {
        var rules = routes
            .Where(r => r.Stack is not null)
            .Select(r => new CloudflareIngressRule {
                Hostname = r.Domain,
                Service = $"http://{ProxyIngressNetworks.EdgeAlias(r.Stack!.ComposeProjectName, r.ServiceName)}:{r.ContainerPort}",
            })
            .OrderBy(r => r.Hostname, StringComparer.Ordinal)
            .ToList();
        rules.Add(new CloudflareIngressRule { Service = "http_status:404" });
        return rules;
    }
}
