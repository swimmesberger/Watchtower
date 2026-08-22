using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// One row of the in-process proxy's routing table: everything the request path needs about a host,
/// flattened out of the <see cref="ProxySite"/> projection so no database is touched per request.
/// <paramref name="Local"/> marks a host Watchtower serves itself (a realm's login page) — those are
/// never forwarded upstream. <paramref name="RouteId"/> is null exactly for those synthesized hosts.
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
/// An immutable routing table: host ⇒ upstream, plus the set of hosts that want a certificate.
/// Built once per reconcile and swapped in wholesale, so a request never observes a half-applied
/// route change and lookups need no locking.
/// </summary>
public sealed class ProxyRouteTableSnapshot {
    /// <summary>The table of a proxy that serves nothing — the disabled and torn-down state.</summary>
    public static readonly ProxyRouteTableSnapshot Empty =
        new(FrozenDictionary<string, ProxyRouteSnapshot>.Empty, []);

    private readonly FrozenDictionary<string, ProxyRouteSnapshot> _byHost;

    internal ProxyRouteTableSnapshot(
        FrozenDictionary<string, ProxyRouteSnapshot> byHost, IReadOnlyList<string> tlsHosts) {
        _byHost = byHost;
        TlsHosts = tlsHosts;
    }

    /// <summary>How many distinct hosts this table serves.</summary>
    public int Count => _byHost.Count;

    /// <summary>
    /// The distinct hosts that need a certificate, lowercased. Realm login hosts are included — they
    /// are served over HTTPS by Watchtower itself and would otherwise be the one set of hosts nobody
    /// could reach securely.
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
/// <see cref="YarpProxyProvider.ApplyAsync"/> on every route change — ADR-0017.
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
    /// Projects the provider-independent site list onto a routing table. Pure, so the routing rules
    /// are testable without a database. Hosts are lowercased; on a duplicate domain the first site
    /// wins, matching <see cref="ProxySiteProjection"/>'s own precedence (an explicit route row is
    /// projected before the login host that would shadow it).
    /// </summary>
    public static ProxyRouteTableSnapshot From(IReadOnlyList<ProxySite> sites) {
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
        return new ProxyRouteTableSnapshot(byHost.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), tlsHosts);
    }
}
