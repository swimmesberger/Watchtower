using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// One row of the in-process proxy's routing table: everything the request path needs about a host,
/// flattened out of the <see cref="ProxySite"/> projection so no database is touched per request.
/// <paramref name="Local"/> marks a host Watchtower serves itself (a <see cref="RouteTarget.Watchtower"/>
/// route) — those are never forwarded upstream. <paramref name="RouteId"/> is set on every row: since
/// ADR-0023 there is one route row per served hostname, Watchtower's own included.
/// </summary>
public sealed record ProxyRouteSnapshot(
    string Host,
    int? RouteId,
    string UpstreamHost,
    int UpstreamPort,
    bool Tls,
    bool Protected,
    IdentityHeaderMode Mode,
    string? BypassPaths,
    bool Local);

/// <summary>
/// One row of the port-bound half of the routing table (ADR-0033): everything the request path needs
/// about a connection that arrived on <paramref name="Port"/>. Deliberately far smaller than
/// <see cref="ProxyRouteSnapshot"/> — a port route is Public by construction and Watchtower is never its
/// upstream, so there is no access mode, no identity headers and no local flag to carry.
/// </summary>
public sealed record ProxyPortRouteSnapshot(int Port, int RouteId, string UpstreamHost, int UpstreamPort);

/// <summary>
/// An immutable routing table: host ⇒ upstream and port ⇒ upstream, plus the set of hosts that want a
/// certificate. Built once per reconcile and swapped in wholesale, so a request never observes a
/// half-applied route change and lookups need no locking.
/// </summary>
public sealed class ProxyRouteTableSnapshot {
    /// <summary>The table of a proxy that serves nothing — the disabled and torn-down state.</summary>
    public static readonly ProxyRouteTableSnapshot Empty =
        new(FrozenDictionary<string, ProxyRouteSnapshot>.Empty, [], FrozenDictionary<int, ProxyPortRouteSnapshot>.Empty);

    private readonly FrozenDictionary<string, ProxyRouteSnapshot> _byHost;
    private readonly FrozenDictionary<int, ProxyPortRouteSnapshot> _byPort;

    internal ProxyRouteTableSnapshot(
        FrozenDictionary<string, ProxyRouteSnapshot> byHost,
        IReadOnlyList<string> tlsHosts,
        FrozenDictionary<int, ProxyPortRouteSnapshot> byPort) {
        _byHost = byHost;
        _byPort = byPort;
        TlsHosts = tlsHosts;
    }

    /// <summary>How many distinct hosts this table serves.</summary>
    public int Count => _byHost.Count;

    /// <summary>The ports this table serves a route on — the set the listener projection is derived from.</summary>
    public IReadOnlyCollection<int> PortRoutePorts => _byPort.Keys;

    /// <summary>Every port-bound row in the table.</summary>
    public IEnumerable<ProxyPortRouteSnapshot> PortRows => _byPort.Values;

    /// <summary>
    /// Looks up the route a connection's local port belongs to. Asked <em>before</em> the host lookup on
    /// every request: a client dialling a bare LAN address sends whatever <c>Host</c> it likes, and on a
    /// port route that header decides nothing.
    /// </summary>
    public bool TryGetByPort(int port, [NotNullWhen(true)] out ProxyPortRouteSnapshot? row) =>
        _byPort.TryGetValue(port, out row);

    /// <summary>
    /// The distinct hosts that need a certificate, lowercased. Watchtower's own hosts are included —
    /// they are served over HTTPS by Watchtower itself and would otherwise be the one set of hosts
    /// nobody could reach securely.
    /// </summary>
    public IReadOnlyList<string> TlsHosts { get; }

    /// <summary>Every row in the table.</summary>
    public IEnumerable<ProxyRouteSnapshot> Rows => _byHost.Values;

    /// <summary>
    /// Looks a host up. Case-insensitive, because hostnames are; a null or blank host is simply not a
    /// match rather than an error — the request path must be able to ask about anything a client sends.
    /// </summary>
    /// <remarks>
    /// Expects a <b>bare</b> host: no port, no trailing dot, no surrounding whitespace. Normalizing a
    /// raw <c>Host</c> header into that form is the middleware's job, not this table's — it is the
    /// layer that knows the listener's own port and can strip it correctly, and doing it here would
    /// mean re-parsing the same string on every request for the benefit of one caller.
    /// </remarks>
    public bool TryGet(string? host, [NotNullWhen(true)] out ProxyRouteSnapshot? row) {
        row = null;
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (!_byHost.TryGetValue(host, out var found)) return false;
        row = found;
        return true;
    }
}

/// <summary>
/// The in-process proxy's routing table: a singleton holding the current
/// <see cref="ProxyRouteTableSnapshot"/>, replaced wholesale by
/// <see cref="YarpProxyProvider.ApplyAsync"/> on every route change — ADR-0022.
/// </summary>
/// <remarks>
/// The read is the request hot path and the write happens once per reconcile, so the concurrency
/// story is deliberately the cheapest one that is correct: a volatile reference swap. Readers get
/// either the whole old table or the whole new one, never a mixture, and no reader ever blocks.
/// </remarks>
public sealed class ProxyRouteTable {
    private volatile ProxyRouteTableSnapshot _current = ProxyRouteTableSnapshot.Empty;

    /// <summary>The table in force right now.</summary>
    public ProxyRouteTableSnapshot Current => _current;

    /// <summary>Swaps in a newly projected table.</summary>
    public void Replace(ProxyRouteTableSnapshot next) => _current = next;

    /// <summary>
    /// Projects the provider-independent site lists onto a routing table. Pure, so the routing rules
    /// are testable without a database. Hosts are lowercased; on a duplicate domain — or a duplicate
    /// port — the first site wins, a defensive rule rather than a load-bearing one, since the filtered
    /// unique indexes on <c>routes.domain</c> and <c>routes.listen_port</c> mean the projection cannot
    /// produce two sites for one address.
    /// </summary>
    /// <param name="portSites">
    /// The port-bound routes (ADR-0033). Their hosts never enter <see cref="ProxyRouteTableSnapshot.TlsHosts"/>:
    /// they have no hostname, and their certificate comes from the internal CA rather than from the ACME
    /// desired set this feeds.
    /// </param>
    public static ProxyRouteTableSnapshot From(
        IReadOnlyList<ProxySite> sites, IReadOnlyList<ProxyPortSite>? portSites = null) {
        var byHost = new Dictionary<string, ProxyRouteSnapshot>(StringComparer.OrdinalIgnoreCase);
        var tlsHosts = new List<string>();
        foreach (var site in sites) {
            if (string.IsNullOrWhiteSpace(site.Domain)) continue;
            var host = site.Domain.Trim().ToLowerInvariant();
            if (byHost.ContainsKey(host)) continue;
            byHost[host] = new ProxyRouteSnapshot(
                host,
                site.RouteId,
                site.UpstreamHost,
                site.UpstreamPort,
                site.Tls,
                site.Protected,
                site.Mode,
                site.BypassPaths,
                site.Local);
            if (site.Tls) tlsHosts.Add(host);
        }

        var byPort = new Dictionary<int, ProxyPortRouteSnapshot>();
        foreach (var site in portSites ?? []) {
            if (byPort.ContainsKey(site.ListenPort)) continue;
            byPort[site.ListenPort] = new ProxyPortRouteSnapshot(
                site.ListenPort, site.RouteId, site.UpstreamHost, site.UpstreamPort);
        }

        return new ProxyRouteTableSnapshot(
            byHost.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), tlsHosts, byPort.ToFrozenDictionary());
    }
}
