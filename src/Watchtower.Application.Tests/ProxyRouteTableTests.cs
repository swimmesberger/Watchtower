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
        var table = ProxyRouteTable.From([Site("App.Example.Invalid")]);

        Assert.True(table.TryGet("app.EXAMPLE.invalid", out var row));
        Assert.Equal("app.example.invalid", row.Host);
        Assert.Equal("app.example.invalid", Assert.Single(table.Rows).Host);
    }

    [Fact]
    public void AnUnknownOrBlankHost_IsNotAMatch() {
        var table = ProxyRouteTable.From([Site("app.example.invalid")]);

        // Anything a client can put in a Host header has to be answerable, including nothing at all.
        Assert.False(table.TryGet(null, out _));
        Assert.False(table.TryGet("", out _));
        Assert.False(table.TryGet("   ", out _));
        Assert.False(table.TryGet("other.example.invalid", out _));
    }

    [Fact]
    public void TlsHosts_CoverTlsRoutesAndLoginHosts_ButNotPlainHttpRoutes() {
        var table = ProxyRouteTable.From([
            Site("secure.example.invalid"),
            Site("plain.example.invalid", tls: false),
            new ProxySite("Login.Example.Invalid", ProxySiteProjection.SelfAlias, ProxySiteProjection.SelfPort,
                Tls: true, Local: true),
        ]);

        // The login host is Watchtower's own, and it is exactly the host that must not be the one
        // nobody can reach over HTTPS.
        Assert.Equal(["secure.example.invalid", "login.example.invalid"], table.TlsHosts);
    }

    [Fact]
    public void ADuplicateDomain_ResolvesToTheFirstSite() {
        // ProxySiteProjection emits the explicit route row before the login host that would shadow it,
        // so "first wins" is what honours the operator's own configuration.
        var table = ProxyRouteTable.From([
            Site("app.example.invalid", upstream: "wanted"),
            Site("APP.example.invalid", upstream: "shadow"),
        ]);

        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet("app.example.invalid", out var row));
        Assert.Equal("wanted", row.UpstreamHost);
        Assert.Equal(["app.example.invalid"], table.TlsHosts);
    }

    [Fact]
    public void TheRouteRowCarriesTheAccessDecisionsThrough() {
        var table = ProxyRouteTable.From([
            new ProxySite("app.example.invalid", "billing-web", 3000, Tls: true, Protected: true,
                Mode: IdentityHeaderMode.Remote, RouteId: 42, BypassPaths: "/hooks", Local: false),
        ]);

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
        Assert.False(ProxyRouteTableSnapshot.Empty.TryGet("app.example.invalid", out _));
    }

    [Fact]
    public void ReplaceSwapsTheWholeTable() {
        var table = new ProxyRouteTable();
        Assert.Same(ProxyRouteTableSnapshot.Empty, table.Current);

        table.Replace(ProxyRouteTable.From([Site("app.example.invalid")]));
        Assert.True(table.Current.TryGet("app.example.invalid", out _));

        // Disabling the provider empties the table; nothing is left half-applied.
        table.Replace(ProxyRouteTableSnapshot.Empty);
        Assert.Equal(0, table.Current.Count);
    }

    private static ProxySite Site(string domain, bool tls = true, string upstream = "billing-web") =>
        new(domain, upstream, 8080, tls, RouteId: 1);
}
