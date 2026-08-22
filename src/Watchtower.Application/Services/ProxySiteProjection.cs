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
/// delegating it to a forward-auth endpoint; both are absent for a synthesized login-host site.
/// <paramref name="Local"/> marks a site Watchtower serves itself (a login host) — it is never
/// forwarded to a stack and never protected.
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
    /// Projects the route table onto the site list, adding a Watchtower self-route for every login host
    /// that needs one. A pure function so the access-control decisions can be tested without a database
    /// or a Docker daemon.
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
    /// realm's <see cref="Realm.AuthHost"/>. They are marked <see cref="ProxySite.Local"/>: Watchtower
    /// serves them itself.
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
    public static IReadOnlyList<ProxySite> Project(
        IReadOnlyList<Route> routes, AuthOptions auth, IReadOnlyList<string> realmAuthHosts) {
        var sites = routes
            .Where(r => r.Stack is not null)
            .Select(r => new ProxySite(
                r.Domain,
                ProxyIngressNetworks.EdgeAlias(r.Stack!.ComposeProjectName, r.ServiceName),
                r.ContainerPort,
                r.TlsEnabled,
                // Customer-owned domains use on-demand TLS; managed subdomains are issued proactively.
                OnDemand: r.Kind == DomainKind.Custom,
                Protected: auth.Enabled && r.AccessMode != AccessMode.Public,
                // Only read for a protected site; the route's mode decides which plaintext headers it forwards.
                Mode: r.IdentityHeaderMode,
                RouteId: r.Id,
                BypassPaths: r.BypassPaths))
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
            if (existing < 0) sites.Add(new ProxySite(host, SelfAlias, SelfPort, Tls: true, Local: true));
            else sites[existing] = sites[existing] with { Protected = false };
        }

        return sites;
    }
}
