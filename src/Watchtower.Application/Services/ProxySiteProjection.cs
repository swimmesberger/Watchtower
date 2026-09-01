using Watchtower.Application.Config;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>
/// A single site the proxy serves: a public <paramref name="Domain"/> forwarded to an internal
/// upstream. <paramref name="UpstreamHost"/> is the container's DNS alias on the ingress network.
/// <paramref name="OnDemand"/> requests a certificate lazily on first request (for customer-owned
/// custom domains), gated by the ask endpoint in <c>CaddyGlobals</c>; it is Caddy-only and
/// ignored by providers that do not manage certificates that way.
/// <paramref name="Protected"/> puts the site behind Watchtower's access control
/// (docs/central-auth/design.md §6). <paramref name="Mode"/> selects which plaintext identity headers the
/// upstream receives on a verified request; it is only consulted for a protected site.
/// <paramref name="RouteId"/> and <paramref name="BypassPaths"/> carry the originating
/// <see cref="Route"/> through to providers that enforce access control themselves rather than
/// delegating it to a forward-auth endpoint. Every site has a <paramref name="RouteId"/>: there is one
/// row per served hostname (ADR-0023), Watchtower's own hostnames included.
/// <paramref name="Local"/> marks a site Watchtower serves itself (a <see cref="RouteTarget.Watchtower"/>
/// route) — it is never forwarded to a stack and never protected.
/// </summary>
public sealed record ProxySite(
    string Domain,
    string UpstreamHost,
    int UpstreamPort,
    bool Tls,
    bool OnDemand = false,
    bool Protected = false,
    IdentityHeaderMode Mode = IdentityHeaderMode.None,
    int? RouteId = null,
    string? BypassPaths = null,
    bool Local = false);

/// <summary>
/// One port-bound route the in-process proxy serves (ADR-0033): a dedicated TLS
/// <paramref name="ListenPort"/> on this host forwarded to a stack service, with no hostname anywhere in
/// it. Deliberately a separate shape from <see cref="ProxySite"/> rather than one with a nullable
/// <c>Domain</c>: every field a site carries for a host — on-demand TLS, the access mode, the identity
/// headers — is meaningless here, and the providers that build host-shaped configuration must not have to
/// remember to skip these rows.
/// </summary>
public sealed record ProxyPortSite(int ListenPort, string UpstreamHost, int UpstreamPort, int RouteId);

/// <summary>
/// Projects Watchtower's route table onto the list of sites a reverse-proxy provider should serve.
/// Pure and provider-independent: <see cref="CaddyManager"/> renders the result as a Caddyfile, and any
/// other provider consumes the same list, so the access-control decisions below are made in exactly one
/// place regardless of which proxy is active.
/// </summary>
public static class ProxySiteProjection {
    /// <summary>DNS alias Watchtower itself answers on inside the container network.</summary>
    public const string SelfAlias = "watchtower";
    /// <summary>Port Watchtower listens on inside its container; where the proxy reaches it.</summary>
    public const int SelfPort = 8080;

    /// <summary>
    /// Projects the route table onto the site list. A pure function so the access-control decisions can be
    /// tested without a database or a Docker daemon.
    /// </summary>
    /// <remarks>
    /// A route is protected only when access control is switched on <em>and</em> the route asks for it, so
    /// turning <c>Auth:Enabled</c> off is a complete escape hatch: the next reconcile emits exactly the
    /// configuration this file produced before access control existed, whatever the route rows say.
    /// <para>
    /// <b><see cref="ProxySite.Local"/> is derived from the target and from nothing else</b> (ADR-0023).
    /// A <see cref="RouteTarget.Watchtower"/> row says the operator wants this instance served on that
    /// hostname; it is emitted pointing at <see cref="SelfAlias"/>:<see cref="SelfPort"/>, which each
    /// provider renders its own way — the in-process proxy hands the request to Watchtower's own pipeline,
    /// Caddy writes <c>reverse_proxy watchtower:8080</c>, and Cloudflare cannot serve it at all and says so
    /// on the route. There is no longer any synthesis here: every served hostname is a row, so every one of
    /// them has a status, a TLS toggle, a certificate and an audit trail.
    /// </para>
    /// <para>
    /// <b>The invariant: no realm's login host may sit behind its own gate.</b> Putting a login page behind
    /// the forward-auth that redirects to that login page is a closed loop whose only way out is the
    /// published port. It used to be enforced here, by force-unprotecting any route on a login host; it is
    /// now structural — <c>ck_routes_target</c> refuses to store a Watchtower route that is not
    /// <see cref="AccessMode.Public"/>, and the projection simply never marks one <c>Protected</c>.
    /// Watchtower authenticates its own UI natively (design.md §2.5), so nothing is lost.
    /// </para>
    /// </remarks>
    /// <param name="routes">The route table. Service rows need <see cref="Route.Stack"/> loaded.</param>
    /// <param name="auth">Access-control settings; <c>Enabled</c> is the master switch read here.</param>
    public static IReadOnlyList<ProxySite> Project(IReadOnlyList<Route> routes, AuthOptions auth) {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(auth);

        var sites = new List<ProxySite>(routes.Count);
        foreach (var r in routes) {
            // A route addressed by port has no hostname to put in a site block, and every provider that
            // consumes this list is host-shaped. Skipped here rather than filtered by each of them, so
            // Caddy and Cloudflare stay exactly as they were; ProjectPortRoutes is the other half.
            // The null check is the compiler's rather than a real branch — ck_routes_binding stores a
            // hostname on every Domain row — and it is a skip for the same reason a stack-less service row
            // is: a projection is not the place to throw about a row that cannot exist.
            if (r.Binding != RouteBinding.Domain || r.Domain is not { } domain) continue;

            if (r.Target == RouteTarget.Watchtower) {
                sites.Add(new ProxySite(
                    domain,
                    SelfAlias,
                    SelfPort,
                    Tls: r.TlsEnabled,
                    // Customer-owned domains use on-demand TLS here too: a realm's login host is very
                    // often a domain the customer owns.
                    OnDemand: r.Kind == DomainKind.Custom,
                    Protected: false,
                    Mode: IdentityHeaderMode.None,
                    RouteId: r.Id,
                    BypassPaths: null,
                    Local: true));
                continue;
            }

            // A service row whose stack has gone is not projectable — there is no upstream to name. It
            // cannot normally happen (the foreign key cascades), but the projection is not the place to
            // throw about it.
            if (r.Stack is null) continue;

            sites.Add(new ProxySite(
                domain,
                ProxyIngressNetworks.EdgeAlias(r.Stack.ComposeProjectName, r.ServiceName),
                r.ContainerPort,
                r.TlsEnabled,
                // Customer-owned domains use on-demand TLS; managed subdomains are issued proactively.
                OnDemand: r.Kind == DomainKind.Custom,
                Protected: auth.Enabled && r.AccessMode != AccessMode.Public,
                // Only read for a protected site; the route's mode decides which plaintext headers it forwards.
                Mode: r.IdentityHeaderMode,
                RouteId: r.Id,
                BypassPaths: r.BypassPaths));
        }

        return sites;
    }

    /// <summary>
    /// Projects the port-bound routes (ADR-0033) onto the listeners the in-process proxy serves. The
    /// counterpart to <see cref="Project"/> over the same table, and pure for the same reason.
    /// </summary>
    /// <remarks>
    /// There is no access-control argument here and no <c>Protected</c> flag: <c>ck_routes_binding</c>
    /// stores a port route as <see cref="AccessMode.Public"/> or not at all, because the forward-auth
    /// flow redirects a visitor to a login page and back to the address they came from — and a bare
    /// <c>host:port</c> is not an address the central login can return anyone to.
    /// </remarks>
    /// <param name="routes">The route table. Port rows need <see cref="Route.Stack"/> loaded.</param>
    public static IReadOnlyList<ProxyPortSite> ProjectPortRoutes(IReadOnlyList<Route> routes) {
        ArgumentNullException.ThrowIfNull(routes);

        var sites = new List<ProxyPortSite>();
        foreach (var r in routes) {
            if (r.Binding != RouteBinding.Port || r.ListenPort is not { } port) continue;
            // Same rule as a service site: a row whose stack has gone names no upstream. The foreign key
            // cascades, so this is the window between the two rather than a state anything settles in.
            if (r.Stack is null) continue;

            sites.Add(new ProxyPortSite(
                port,
                ProxyIngressNetworks.EdgeAlias(r.Stack.ComposeProjectName, r.ServiceName),
                r.ContainerPort,
                r.Id));
        }

        return sites;
    }
}
