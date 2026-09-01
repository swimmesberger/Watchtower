using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Watchtower.Application.Config;
using Watchtower.Application.Services.PortRoutes;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// The configuration Kestrel is handed — and the one place that decides whether the in-process proxy's
/// ingress listeners exist (ADR-0022, runtime-bound ingress addendum).
/// </summary>
/// <remarks>
/// <para>
/// It projects the host's <c>Kestrel</c> section with the two proxy endpoints replaced: everything under
/// <c>Kestrel</c> passes through <em>except</em> <c>Endpoints:ProxyHttp*</c>, and the proxy endpoints are
/// derived instead from the reverse-proxy settings — <c>Proxy:Enabled</c>, <c>Proxy:Provider == yarp</c>
/// and the two port settings. So the listeners follow the settings: enabling the proxy binds them,
/// disabling it or switching to Caddy/Cloudflare unbinds them, moving a port rebinds it, all without a
/// restart. A Caddy or Cloudflare deployment carries no idle TLS listener at all.
/// </para>
/// <para>
/// The port-route listeners (ADR-0033) are projected here too, and they are <em>not</em> on the provider
/// gate: <c>Proxy:Enabled</c> is the whole condition, because a port route's listener is on Watchtower's
/// own container whatever terminates the public domains (ADR-0033 addendum). So a Caddy or Cloudflare
/// deployment carries exactly the listeners its port routes ask for and no others.
/// </para>
/// <para>
/// Masking the operator's own <c>Kestrel:Endpoints:ProxyHttp*</c> keys is the point of the exception, not
/// a side effect. Those keys used to be the knob (the shipped image set them, and blanking one was how you
/// turned a listener off — Docker cannot unset a baked-in variable, only override it with an empty value,
/// which Kestrel's loader rejects outright). They are now derived, so a stale value left in a compose file
/// must not reach the loader: it would either fight the derivation or fail the host on startup. Masking
/// retires the "blank means off" filter along with it.
/// </para>
/// <para>
/// Reload-aware: the projection subscribes to the root configuration's reload token — the Elarion settings
/// store is a reloadable source layered under the environment (ADR-0014), so a settings write reaches
/// here — and raises its own token only when the projected key set actually changed. That guard is what
/// keeps the root-token → project → reload chain from turning every unrelated settings write into a
/// Kestrel rebind.
/// </para>
/// <para>
/// Everything read out of the root is treated as operator input that may be wrong: an unparseable
/// <c>Proxy:Enabled</c> is <see langword="false"/> and an unparseable port is <em>off</em> rather than the
/// shipped default. A typo in an environment variable must not throw inside <see cref="Build"/>, which
/// runs before the host exists and would take the process down with a stack trace instead of a listener.
/// </para>
/// </remarks>
public static class ProxyIngressKestrelConfiguration {
    /// <summary>The plain-HTTP ingress endpoint's Kestrel name.</summary>
    public const string HttpEndpointName = "ProxyHttp";

    /// <summary>The TLS ingress endpoint's Kestrel name — the one with the SNI callback.</summary>
    public const string HttpsEndpointName = "ProxyHttps";

    /// <summary>The management endpoint's Kestrel name: Watchtower's own UI and API. Never ingress.</summary>
    public const string ManagementEndpointName = "Http";

    /// <summary>The section of <paramref name="root"/> the projection reads and passes through.</summary>
    private const string KestrelSection = "Kestrel";

    private const string ManagementUrlKey = $"Endpoints:{ManagementEndpointName}:Url";

    /// <summary>
    /// The projected section, ready to hand to <c>KestrelServerOptions.Configure(..., reloadOnChange: true)</c>.
    /// Keys are relative to <c>Kestrel</c> (<c>Endpoints:ProxyHttps:Url</c>), which is what the loader reads.
    /// </summary>
    /// <param name="root">The host's configuration, settings-store layer included.</param>
    /// <param name="warnings">
    /// Where a conflict between an ingress port and the management port is reported. Optional so the pure
    /// tests do not have to supply one.
    /// </param>
    public static IConfigurationRoot Build(IConfiguration root, ProxyIngressWarnings? warnings = null) {
        ArgumentNullException.ThrowIfNull(root);
        return new ConfigurationBuilder().Add(new ProjectedSource(root, warnings)).Build();
    }

    /// <summary>
    /// The ingress ports the reverse-proxy settings call for — <see langword="null"/> for a listener that
    /// should not exist. Pure: no reload, no state, and the single definition of "is there ingress?".
    /// </summary>
    /// <remarks>
    /// Deliberately blind to the management port: that collision is resolved in the projection, where
    /// there is somewhere to report it. This answers only "what did the operator ask for?".
    /// </remarks>
    public static (int? HttpPort, int? HttpsPort) DerivePorts(IConfiguration root) {
        ArgumentNullException.ThrowIfNull(root);
        if (!IsInProcessProxyActive(root)) return (null, null);

        return (
            Port(root, WatchtowerSettingPaths.ProxyYarpHttpPort, YarpProxyOptions.DefaultHttpPort),
            Port(root, WatchtowerSettingPaths.ProxyYarpHttpsPort, YarpProxyOptions.DefaultHttpsPort));
    }

    /// <summary>
    /// The listen ports of the port-bound routes (ADR-0033) — one dedicated TLS listener each, gated on
    /// <c>Proxy:Enabled</c> and on nothing else. Pure, like <see cref="DerivePorts"/>, and blind to
    /// collisions for the same reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not on the provider</b> (ADR-0033 addendum). These listeners are on Watchtower's own container
    /// and forward over the stack's ingress network; a sibling Caddy container and a Cloudflare Tunnel
    /// terminate the public <em>domains</em> and have no say in it. Gating them on <c>yarp</c> is what
    /// used to make "LAN HTTPS" and "which provider serves my domains" the same question, which they
    /// never were.
    /// </para>
    /// <para>
    /// Read straight from the setting rather than from the route table: this runs before the host and its
    /// database exist, and <see cref="PortRoutes.PortRoutePlane"/> is the one writer that keeps the
    /// setting in step with the rows.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> DerivePortRoutePorts(IConfiguration root) {
        ArgumentNullException.ThrowIfNull(root);
        if (!IsProxyEnabled(root)) return [];
        return PortRouteListeners.Parse(root[WatchtowerSettingPaths.ProxyPortRoutesPorts]);
    }

    /// <summary>
    /// Whether the reverse proxy is on at all — the gate the port-route listeners are behind, and the
    /// first half of the ingress gate.
    /// </summary>
    /// <remarks>
    /// Not <c>GetValue&lt;bool&gt;</c>: that throws on a value it cannot convert, and this runs before
    /// the host exists — a typo'd <c>WATCHTOWER__PROXY__ENABLED</c> would be a stack trace at startup
    /// rather than a proxy that stays off.
    /// </remarks>
    private static bool IsProxyEnabled(IConfiguration root) =>
        bool.TryParse(root["Watchtower:Proxy:Enabled"], out var enabled) && enabled;

    /// <summary>Whether the in-process proxy is the one serving — the gate the ingress listeners are behind.</summary>
    private static bool IsInProcessProxyActive(IConfiguration root) {
        if (!IsProxyEnabled(root)) return false;

        // Resolved through ProxyOptions so "unknown or blank means yarp" is stated once, next to the
        // provider names, rather than re-derived here where it could drift.
        var provider = new ProxyOptions { Provider = root["Watchtower:Proxy:Provider"] ?? "" }.ResolveProvider();
        return provider == ProxyProviderKind.Yarp;
    }

    /// <summary>
    /// One port setting: absent or blank falls back to the shipped default (the same value
    /// <see cref="YarpProxyOptions"/> binds); <c>0</c>, a value out of range and anything that is not a
    /// number at all mean "no listener". A typo is deliberately <em>not</em> read as the default — binding
    /// a public port the operator did not name is the worse of the two failures.
    /// </summary>
    private static int? Port(IConfiguration root, string path, int fallback) {
        var configured = root[path];
        if (string.IsNullOrWhiteSpace(configured)) return fallback;
        return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            && port is > 0 and <= 65535
                ? port
                : null;
    }

    /// <summary>The projected key/value pairs for a given moment of the root configuration.</summary>
    private static Dictionary<string, string?> Project(IConfiguration root, ProxyIngressWarnings? warnings) {
        var projected = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Everything the host configured under Kestrel, minus the two derived endpoints. Section markers
        // carry no value of their own and are implied by the keys underneath them.
        foreach (var (key, value) in root.GetSection(KestrelSection).AsEnumerable(makePathsRelative: true)) {
            if (value is null || IsProxyEndpointKey(key)) continue;
            projected[key] = value;
        }

        var (httpPort, httpsPort) = DerivePorts(root);
        var portRoutePorts = DerivePortRoutePorts(root);
        // A deployment that serves nothing but port routes runs with both ingress ports off, so the port
        // routes count towards "this host has ingress" on their own.
        if (httpPort is null && httpsPort is null && portRoutePorts.Count == 0) return projected;

        // From here on this host has ingress, which makes the management endpoint's own URL load-bearing
        // in two ways.
        //
        // First, it has to exist as a *named endpoint*. Kestrel binds the hosting URLs
        // (ASPNETCORE_URLS, launchSettings, UseUrls) only while no endpoint is configured at all — the
        // moment the proxy adds one they are overridden with a warning. A bare `dotnet run` or systemd
        // host that enables the proxy would otherwise lose its management listener at the next start, so
        // the hosting URL is promoted into Endpoints:Http:Url here. Only on this branch: with the proxy
        // off nothing is projected and development is untouched.
        var managementUrl = projected.GetValueOrDefault(ManagementUrlKey);
        if (string.IsNullOrWhiteSpace(managementUrl) && FirstPlainHttpUrl(root) is { } hostingUrl) {
            managementUrl = hostingUrl;
            projected[ManagementUrlKey] = hostingUrl;
        }

        // Second, an ingress port that collides with it is dropped rather than bound. Two endpoints on one
        // port is a duplicate bind, and "the management port is also ingress" is the exact confusion the
        // endpoint split exists to prevent — an unrouted host would stop reaching the UI. It is reachable
        // by accident: WATCHTOWER__PROXY__YARP__HTTPPORT=8080 against the shipped image, say.
        var managementPort = ListenerUrl.PortOf(managementUrl);
        if (httpPort is { } http && http == managementPort) {
            warnings?.Warn(
                $"Ingress HTTP port {http} is the management port; the ingress listener is not bound. "
                + "Give Watchtower:Proxy:Yarp:HttpPort a port of its own.");
            httpPort = null;
        }
        if (httpsPort is { } https && https == managementPort) {
            warnings?.Warn(
                $"Ingress HTTPS port {https} is the management port; the ingress listener is not bound. "
                + "Give Watchtower:Proxy:Yarp:HttpsPort a port of its own.");
            httpsPort = null;
        }

        // "+" — every interface. Operators publish container ports; picking an interface inside the
        // container would only be a way to publish nothing.
        if (httpPort is { } httpBind)
            projected[$"Endpoints:{HttpEndpointName}:Url"] =
                string.Create(CultureInfo.InvariantCulture, $"http://+:{httpBind}");
        if (httpsPort is { } httpsBind)
            projected[$"Endpoints:{HttpsEndpointName}:Url"] =
                string.Create(CultureInfo.InvariantCulture, $"https://+:{httpsBind}");

        // One TLS listener per port route (ADR-0033), named after its port so the set of endpoints is a
        // function of the set of ports and a route that goes away takes its endpoint with it. The
        // certificate is not named here: these endpoints carry no Certificate section at all and are made
        // TLS by the endpoint defaults, which is what lets an endpoint appear at runtime under a name
        // nobody registered a callback for. Not by ConfigureHttpsDefaults — that was the approach this
        // one replaced, and naming it here is how a reader would reconstruct it.
        foreach (var port in portRoutePorts) {
            // A route's port that is already somebody's listener is dropped rather than bound: a second
            // endpoint on one port is a duplicate bind, and the two collisions worth naming separately are
            // the management plane (which would stop being reachable) and the ingress ports (where the
            // route would be shadowed by the host dispatcher's own listener). Create-time validation
            // refuses these, so reaching here means the ports moved underneath an existing route.
            //
            // These drops are load-bearing twice over, and neither reason may be simplified away.
            // Dispatch: YarpHostDispatchMiddleware decides "is this a port route's listener?" from the
            // endpoints projected here, so a port left in would let one route capture the management or
            // ingress listener wholesale. TLS: ProxyHttpsEndpoint's endpoint-defaults hook keys on the
            // port alone, so a port-route port equal to a *plain-HTTP* endpoint's would convert that
            // listener to TLS. Today the duplicate bind fails the host first, which hides both — but the
            // reasoning has to outlive that accident.
            if (port == managementPort) {
                warnings?.Warn(
                    $"Port route listener {port} is the management port; it is not bound. Move the route "
                    + "to a port of its own.");
                continue;
            }
            if (port == httpPort || port == httpsPort) {
                warnings?.Warn(
                    $"Port route listener {port} is an ingress port; it is not bound. Move the route to a "
                    + "port of its own.");
                continue;
            }
            projected[$"Endpoints:{PortRouteListeners.EndpointName(port)}:Url"] =
                string.Create(CultureInfo.InvariantCulture, $"https://+:{port}");
        }

        return projected;
    }

    /// <summary>
    /// The first plain-HTTP hosting URL (<c>ASPNETCORE_URLS</c> / <c>UseUrls</c>), or null. Plain HTTP
    /// only: an <c>https://</c> hosting URL carries no certificate configuration this projection could
    /// reproduce, and the management endpoint is the one an operator binds privately anyway.
    /// </summary>
    private static string? FirstPlainHttpUrl(IConfiguration root) => root[WebHostDefaults.ServerUrlsKey]?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a <c>Kestrel</c>-relative key belongs to one of the derived endpoints. The trailing colon
    /// matters: without it <c>Endpoints:ProxyHttp</c> would also swallow <c>Endpoints:ProxyHttps:Url</c>.
    /// The port routes' endpoints are matched on the whole name instead — their names end in a number, so
    /// a prefix rule would be the same trap one level down.
    /// </summary>
    private static bool IsProxyEndpointKey(string key) =>
        IsUnder(key, $"Endpoints:{HttpEndpointName}")
        || IsUnder(key, $"Endpoints:{HttpsEndpointName}")
        || PortRouteListeners.IsPortEndpointName(EndpointNameOf(key));

    private static bool IsUnder(string key, string prefix) =>
        key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith($"{prefix}:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The endpoint name a <c>Kestrel</c>-relative key names, or null when the key is not under
    /// <c>Endpoints</c> at all — <c>Endpoints:ProxyPort9001:Url</c> is <c>ProxyPort9001</c>.
    /// </summary>
    private static string? EndpointNameOf(string key) {
        const string prefix = "Endpoints:";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = key.AsSpan(prefix.Length);
        var colon = rest.IndexOf(':');
        return (colon < 0 ? rest : rest[..colon]).ToString();
    }

    private sealed class ProjectedSource(IConfiguration root, ProxyIngressWarnings? warnings)
        : IConfigurationSource {
        public IConfigurationProvider Build(IConfigurationBuilder builder) =>
            new ProjectedProvider(root, warnings);
    }

    private sealed class ProjectedProvider : ConfigurationProvider {
        private readonly IConfiguration _root;
        private readonly ProxyIngressWarnings? _warnings;

        public ProjectedProvider(IConfiguration root, ProxyIngressWarnings? warnings) {
            _root = root;
            _warnings = warnings;
            Data = Project(root, warnings);
            // Held for the life of the process, like the configuration it watches — there is nothing to
            // unsubscribe from before the host itself goes away.
            ChangeToken.OnChange(root.GetReloadToken, Reload);
        }

        /// <summary>
        /// Re-projects, and tells Kestrel only when the projection really moved. Every settings write
        /// raises the root's token; almost none of them change a listener, and an unconditional
        /// <c>OnReload</c> would rebind endpoints on each one.
        /// </summary>
        private void Reload() {
            var next = Project(_root, _warnings);
            if (Unchanged(next)) return;
            Data = next;
            OnReload();
        }

        private bool Unchanged(Dictionary<string, string?> next) {
            if (next.Count != Data.Count) return false;
            foreach (var (key, value) in next) {
                if (!Data.TryGetValue(key, out var current)) return false;
                if (!string.Equals(current, value, StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }
}
