using System.Text;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>
/// A single site the proxy serves: a public <paramref name="Domain"/> forwarded to an internal
/// upstream. <paramref name="UpstreamHost"/> is the container's DNS alias on the ingress network.
/// <paramref name="OnDemand"/> requests a certificate lazily on first request (for customer-owned
/// custom domains), gated by the ask endpoint in <see cref="CaddyGlobals"/>.
/// <paramref name="Protected"/> puts the site behind Watchtower's access control
/// (docs/central-auth/design.md §6). <paramref name="Mode"/> selects which plaintext identity headers the
/// upstream receives on a verified request; it is only consulted for a protected site.
/// </summary>
public sealed record CaddySite(
    string Domain,
    string UpstreamHost,
    int UpstreamPort,
    bool Tls,
    bool OnDemand = false,
    bool Protected = false,
    IdentityHeaderMode Mode = IdentityHeaderMode.None);

/// <summary>
/// Global Caddy options that apply to every site. When <paramref name="AskUrl"/> is set, on-demand TLS
/// is enabled and gated by that endpoint (which must return 2xx only for domains Watchtower knows).
/// <paramref name="SelfUpstream"/> is where protected sites send the access-control traffic — Watchtower
/// itself, reachable on the control network.
/// </summary>
public sealed record CaddyGlobals(
    string? Email,
    int AdminPort = 2019,
    string? AskUrl = null,
    string SelfUpstream = "watchtower:8080");

/// <summary>
/// Renders a Caddyfile from Watchtower's route table. Pure and side-effect free so it is trivial to
/// unit-test; <see cref="CaddyManager"/> pushes the result to Caddy's admin API.
/// The generated file always keeps the admin endpoint on <c>0.0.0.0:{AdminPort}</c> so subsequent
/// reloads remain possible (a config without it would close the very endpoint used to push the next one).
/// </summary>
public static class CaddyConfigBuilder {
    public static string Build(IReadOnlyList<CaddySite> sites, CaddyGlobals globals) {
        var sb = new StringBuilder();

        // Global options block — admin must stay reachable on the control network for future reloads.
        sb.Append("{\n");
        sb.Append($"\tadmin 0.0.0.0:{globals.AdminPort}\n");
        if (!string.IsNullOrWhiteSpace(globals.Email))
            sb.Append($"\temail {globals.Email}\n");
        if (!string.IsNullOrWhiteSpace(globals.AskUrl)) {
            sb.Append("\ton_demand_tls {\n");
            sb.Append($"\t\task {globals.AskUrl}\n");
            sb.Append("\t}\n");
        }
        sb.Append("}\n");

        foreach (var site in sites.OrderBy(s => s.Domain, StringComparer.Ordinal)) {
            // A non-TLS site is addressed as http:// so Caddy does not attempt automatic HTTPS.
            var address = site.Tls ? site.Domain : $"http://{site.Domain}";
            sb.Append('\n');
            sb.Append($"{address} {{\n");
            // On-demand: fetch the cert lazily on first request (customer-owned domains), gated by `ask`.
            if (site.Tls && site.OnDemand)
                sb.Append("\ttls {\n\t\ton_demand\n\t}\n");

            if (site.Protected)
                AppendProtectedBody(sb, site, globals);
            else
                sb.Append($"\treverse_proxy {site.UpstreamHost}:{site.UpstreamPort}\n");

            sb.Append("}\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The access-controlled shape (design.md §6). Two ordered concerns live here and neither is optional:
    /// <list type="number">
    ///   <item><description>
    ///     <c>/.watchtower/*</c> is served by Watchtower on the app's own domain, so the callback that mints
    ///     the app session and the per-app logout are reachable without the upstream ever seeing them. It is
    ///     matched first because those paths must work while the visitor is still anonymous.
    ///   </description></item>
    ///   <item><description>
    ///     Every forwardable identity name is stripped from the inbound request <em>before</em>
    ///     <c>forward_auth</c> adds the verified ones — the full <see cref="IdentityForwarding.AllForwardableHeaderNames"/>
    ///     union, regardless of this route's mode, so a JWT-only route still strips <c>Remote-User</c> and
    ///     the rest and nothing a client sends under any of those names survives. <c>copy_headers</c> then
    ///     copies back only what this route's mode forwards (the JWT always, plus that mode's plaintext
    ///     names), which is by construction a subset of what was stripped. Emitting a <c>copy_headers</c>
    ///     name without the matching <c>request_header -…</c> line would let any client assert that identity,
    ///     which is the whole reason §2.3 treats these headers as a trust boundary.
    ///   </description></item>
    /// </list>
    /// </summary>
    private static void AppendProtectedBody(StringBuilder sb, CaddySite site, CaddyGlobals globals) {
        sb.Append("\thandle /.watchtower/* {\n");
        sb.Append($"\t\treverse_proxy {globals.SelfUpstream}\n");
        sb.Append("\t}\n");
        sb.Append("\thandle {\n");
        // Strip the whole union, not just this mode's copy set: defense in depth means a client cannot
        // smuggle in a header that some *other* mode would have honoured.
        foreach (var header in IdentityForwarding.AllForwardableHeaderNames)
            sb.Append($"\t\trequest_header -{header}\n");
        sb.Append($"\t\tforward_auth {globals.SelfUpstream} {{\n");
        sb.Append($"\t\t\turi {RouteAccessPolicy.VerifyPath}\n");
        sb.Append($"\t\t\tcopy_headers {string.Join(' ', IdentityForwarding.CopyHeaderNames(site.Mode))}\n");
        sb.Append("\t\t}\n");
        sb.Append($"\t\treverse_proxy {site.UpstreamHost}:{site.UpstreamPort}\n");
        sb.Append("\t}\n");
    }
}
