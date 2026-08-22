using System.Text;

namespace Watchtower.Application.Services;

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
    public static string Build(IReadOnlyList<ProxySite> sites, CaddyGlobals globals) {
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
    ///     The full ecosystem identity+authz namespace (<see cref="IdentityForwarding.StripHeaderNames"/>) is
    ///     stripped from the inbound request <em>before</em> <c>forward_auth</c> adds the verified ones,
    ///     regardless of this route's mode — <c>copy_headers</c> governs only the verify <em>response</em>, so
    ///     every other client header (including <c>Remote-Groups</c>, which a group-aware upstream would
    ///     honour) passes through untouched unless stripped here. The strip set is therefore a deliberate
    ///     superset of what we forward: a JWT-only route still strips <c>Remote-User</c>, <c>Remote-Groups</c>
    ///     and the rest, so nothing a client sends under any of those names survives. <c>copy_headers</c>
    ///     copies back only what this route's mode forwards (the JWT always, plus that mode's plaintext
    ///     names), which is by construction a subset of what was stripped. Emitting a <c>copy_headers</c>
    ///     name without the matching <c>request_header -…</c> line would let any client assert that identity,
    ///     which is the whole reason §2.3 treats these headers as a trust boundary.
    ///   </description></item>
    /// </list>
    /// </summary>
    private static void AppendProtectedBody(StringBuilder sb, ProxySite site, CaddyGlobals globals) {
        sb.Append("\thandle /.watchtower/* {\n");
        sb.Append($"\t\treverse_proxy {globals.SelfUpstream}\n");
        sb.Append("\t}\n");
        sb.Append("\thandle {\n");
        // Strip the whole ecosystem authz namespace, not just this mode's copy set: defense in depth means a
        // client cannot smuggle in a header (a group claim, another mode's name) the upstream would honour.
        foreach (var header in IdentityForwarding.StripHeaderNames)
            sb.Append($"\t\trequest_header -{header}\n");
        sb.Append($"\t\tforward_auth {globals.SelfUpstream} {{\n");
        sb.Append($"\t\t\turi {RouteAccessPolicy.VerifyPath}\n");
        sb.Append($"\t\t\tcopy_headers {string.Join(' ', IdentityForwarding.CopyHeaderNames(site.Mode))}\n");
        sb.Append("\t\t}\n");
        sb.Append($"\t\treverse_proxy {site.UpstreamHost}:{site.UpstreamPort}\n");
        sb.Append("\t}\n");
    }
}
