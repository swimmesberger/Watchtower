using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the in-process proxy's routing table — the structure every request is matched against, so
/// its lookup rules are a correctness concern rather than a convenience: hostnames are
/// case-insensitive, a duplicate domain must resolve to exactly one upstream, and the TLS host set
/// (what the certificate manager is asked to obtain) has to include the realm login pages Watchtower
/// serves itself.
/// </summary>
public sealed class ProxyRouteTableTests {
    [Fact]
    public void HostsAreLowercased_AndLookedUpCaseInsensitively() {
        var table = Hosts(Site("App.Example.Invalid"));

        Assert.True(table.TryGet("app.EXAMPLE.invalid", out var row));
        Assert.Equal("app.example.invalid", row.Host);
        Assert.Equal("app.example.invalid", Assert.Single(table.Rows).Host);
    }

    [Fact]
    public void AnUnknownOrBlankHost_IsNotAMatch() {
        var table = Hosts(Site("app.example.invalid"));

        // Anything a client can put in a Host header has to be answerable, including nothing at all.
        Assert.False(table.TryGet(null, out _));
        Assert.False(table.TryGet("", out _));
        Assert.False(table.TryGet("   ", out _));
        Assert.False(table.TryGet("other.example.invalid", out _));
    }

    [Fact]
    public void TlsHosts_CoverTlsRoutesAndLoginHosts_ButNotPlainHttpRoutes() {
        var table = Hosts(
            Site("secure.example.invalid"),
            Site("plain.example.invalid", tls: false),
            new ProxySite("Login.Example.Invalid", ProxySiteProjection.SelfAlias, ProxySiteProjection.SelfPort,
                Tls: true, Local: true));

        // The login host is Watchtower's own, and it is exactly the host that must not be the one
        // nobody can reach over HTTPS.
        Assert.Equal(["secure.example.invalid", "login.example.invalid"], table.TlsHosts);
    }

    [Fact]
    public void ADuplicateDomain_ResolvesToTheFirstSite() {
        // ProxySiteProjection emits the explicit route row before the login host that would shadow it,
        // so "first wins" is what honours the operator's own configuration.
        var table = Hosts(
            Site("app.example.invalid", upstream: "wanted"),
            Site("APP.example.invalid", upstream: "shadow"));

        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet("app.example.invalid", out var row));
        Assert.Equal("wanted", row.UpstreamHost);
        Assert.Equal(["app.example.invalid"], table.TlsHosts);
    }

    [Fact]
    public void TheRouteRowCarriesTheAccessDecisionsThrough() {
        var table = Hosts(
            new ProxySite("app.example.invalid", "billing-web", 3000, Tls: true, Protected: true,
                Mode: IdentityHeaderMode.Remote, RouteId: 42, BypassPaths: "/hooks", Local: false));

        Assert.True(table.TryGet("app.example.invalid", out var row));
        Assert.Equal(42, row.RouteId);
        Assert.Equal("billing-web", row.UpstreamHost);
        Assert.Equal(3000, row.UpstreamPort);
        Assert.True(row.Protected);
        Assert.Equal(IdentityHeaderMode.Remote, row.Mode);
        Assert.Equal("/hooks", row.BypassPaths);
        Assert.False(row.Local);
    }

    [Fact]
    public void TheEmptyTable_ServesNothing() {
        Assert.Equal(0, ProxyRouteTableSnapshot.Empty.Count);
        Assert.Empty(ProxyRouteTableSnapshot.Empty.TlsHosts);
        Assert.Empty(ProxyRouteTableSnapshot.Empty.PortRoutePorts);
        Assert.False(ProxyRouteTableSnapshot.Empty.TryGet("app.example.invalid", out _));
        Assert.False(ProxyRouteTableSnapshot.Empty.TryGetByPort(9001, out _));
    }

    // ── Port-bound routes (ADR-0033) ──────────────────────────────────────────

    [Fact]
    public void APortRoute_IsFoundByItsPortAndCarriesItsUpstream() {
        var table = Ports(new ProxyPortSite(9001, "media-jellyfin", 8096, RouteId: 7));

        Assert.True(table.TryGetByPort(9001, out var row));
        Assert.Equal(9001, row.Port);
        Assert.Equal(7, row.RouteId);
        Assert.Equal("media-jellyfin", row.UpstreamHost);
        Assert.Equal(8096, row.UpstreamPort);
        Assert.Equal([9001], table.PortRoutePorts);
        Assert.False(table.TryGetByPort(9002, out _));
    }

    /// <summary>
    /// The two halves are independent tables over the same rows: a port route is not reachable by a host
    /// header, and the hosts are not reachable by a port.
    /// </summary>
    [Fact]
    public void ThePortHalfAndTheHostHalf_DoNotSeeEachOther() {
        var table = Both([Site("app.example.invalid")], [new ProxyPortSite(9001, "media-jellyfin", 8096, RouteId: 7)]);

        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet("app.example.invalid", out _));
        Assert.True(table.TryGetByPort(9001, out _));
        // The host half does not answer for the port route's upstream, and the port half is not reachable
        // by the hostname that has one.
        Assert.False(table.TryGet("media-jellyfin", out _));
        Assert.Single(table.PortRoutePorts);
    }

    /// <summary>
    /// The one thing a port route must never do: enter the set the ACME machinery is asked to obtain.
    /// Its certificate comes from Watchtower's own CA, and there is no public authority that would issue
    /// for a name that does not exist — an order for one would be a refusal on a rate-limited endpoint.
    /// </summary>
    [Fact]
    public void APortRoute_NeverEntersTheAcmeDesiredSet() {
        var table = Both([Site("app.example.invalid")], [new ProxyPortSite(9001, "media-jellyfin", 8096, RouteId: 7)]);

        Assert.Equal(["app.example.invalid"], table.TlsHosts);
    }

    /// <summary>
    /// Defensive, like the duplicate-domain rule: the filtered unique index on <c>listen_port</c> means
    /// the projection cannot produce two sites on one port, and a table is not the place to throw about
    /// a row that cannot exist.
    /// </summary>
    [Fact]
    public void ADuplicatePort_ResolvesToTheFirstSite() {
        var table = Ports(
            new ProxyPortSite(9001, "wanted", 8096, 7), new ProxyPortSite(9001, "shadow", 8096, 8));

        Assert.True(table.TryGetByPort(9001, out var row));
        Assert.Equal("wanted", row.UpstreamHost);
        Assert.Single(table.PortRoutePorts);
    }

    // ── Two writers, one snapshot (ADR-0033 addendum) ─────────────────────────

    /// <summary>
    /// The reason the table has two publish operations rather than one <c>Replace</c>: the yarp provider
    /// owns the host half and the port plane owns the port half, and under Caddy or Cloudflare the
    /// provider never writes at all. Each publish has to leave the other half exactly where it was.
    /// </summary>
    [Fact]
    public void PublishingOneHalf_LeavesTheOtherIntact() {
        var table = new ProxyRouteTable();
        Assert.Same(ProxyRouteTableSnapshot.Empty, table.Current);

        table.PublishHostRoutes([Site("app.example.invalid")]);
        table.PublishPortRoutes([new ProxyPortSite(9001, "media-jellyfin", 8096, RouteId: 7)]);

        // One snapshot, both halves — which is what the dispatcher reads, once per request.
        Assert.True(table.Current.TryGet("app.example.invalid", out _));
        Assert.True(table.Current.TryGetByPort(9001, out _));

        // The provider going inactive (a switch to Caddy) empties its half and touches nothing else.
        table.PublishHostRoutes([]);
        Assert.Equal(0, table.Current.Count);
        Assert.True(table.Current.TryGetByPort(9001, out _));
        Assert.Equal([9001], table.Current.PortRoutePorts);

        // …and the same in the other direction: the last port route deleted, the domains still served.
        table.PublishHostRoutes([Site("app.example.invalid")]);
        table.PublishPortRoutes([]);
        Assert.True(table.Current.TryGet("app.example.invalid", out _));
        Assert.Empty(table.Current.PortRoutePorts);
    }

    /// <summary>Both halves empty is the shared empty snapshot, so the dispatcher's fast path stays cheap.</summary>
    [Fact]
    public void WithNeitherHalfPublished_TheTableIsTheEmptyOne() {
        var table = new ProxyRouteTable();
        table.PublishHostRoutes([Site("app.example.invalid")]);
        table.PublishPortRoutes([new ProxyPortSite(9001, "media-jellyfin", 8096, RouteId: 7)]);

        table.PublishHostRoutes([]);
        table.PublishPortRoutes([]);

        Assert.Same(ProxyRouteTableSnapshot.Empty, table.Current);
    }

    private static ProxySite Site(string domain, bool tls = true, string upstream = "billing-web") =>
        new(domain, upstream, 8080, tls, RouteId: 1);

    /// <summary>A table with only the host half published — the shape under Caddy is the mirror of this.</summary>
    private static ProxyRouteTableSnapshot Hosts(params ProxySite[] sites) => Both(sites, []);

    /// <summary>A table with only the port half published — the shape a port-routes-only deployment has.</summary>
    private static ProxyRouteTableSnapshot Ports(params ProxyPortSite[] portSites) => Both([], portSites);

    private static ProxyRouteTableSnapshot Both(
        IReadOnlyList<ProxySite> sites, IReadOnlyList<ProxyPortSite> portSites) {
        var table = new ProxyRouteTable();
        table.PublishHostRoutes(sites);
        table.PublishPortRoutes(portSites);
        return table.Current;
    }
}
