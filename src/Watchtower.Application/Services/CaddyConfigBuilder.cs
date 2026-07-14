using System.Text;

namespace Watchtower.Application.Services;

/// <summary>
/// A single site the proxy serves: a public <paramref name="Domain"/> forwarded to an internal
/// upstream. <paramref name="UpstreamHost"/> is the container's DNS alias on the ingress network.
/// <paramref name="OnDemand"/> requests a certificate lazily on first request (for customer-owned
/// custom domains), gated by the ask endpoint in <see cref="CaddyGlobals"/>.
/// </summary>
public sealed record CaddySite(string Domain, string UpstreamHost, int UpstreamPort, bool Tls, bool OnDemand = false);

/// <summary>
/// Global Caddy options that apply to every site. When <paramref name="AskUrl"/> is set, on-demand TLS
/// is enabled and gated by that endpoint (which must return 2xx only for domains Watchtower knows).
/// </summary>
public sealed record CaddyGlobals(string? Email, int AdminPort = 2019, string? AskUrl = null);

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
            sb.Append($"\treverse_proxy {site.UpstreamHost}:{site.UpstreamPort}\n");
            sb.Append("}\n");
        }

        return sb.ToString();
    }
}
