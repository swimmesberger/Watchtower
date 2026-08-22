using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <c>ProxySiteProjection.Project</c> — which routes end up behind access control, and which
/// hostnames Watchtower serves itself (docs/central-auth/design.md §6, §11; ADR-0021).
/// </summary>
/// <remarks>
/// Since ADR-0021 there is no synthesis here at all: <c>Local</c> is derived from
/// <see cref="RouteTarget.Watchtower"/> and from nothing else, so the tests that used to describe a
/// login host appearing out of configuration now describe a row appearing as a site.
/// </remarks>
public sealed class ProxySiteProjectionTests {
    private const string SelfHost = "watchtower.example.invalid";

    [Fact]
    public void WithAuthDisabled_NothingIsProtected() {
        var sites = ProxySiteProjection.Project(
            [Route("public.example.invalid", AccessMode.Public),
             Route("members.example.invalid", AccessMode.Authenticated),
             Route("secret.example.invalid", AccessMode.Restricted)],
            new AuthOptions { Enabled = false, Host = SelfHost });

        // The escape hatch: turning access control off restores the previous configuration exactly,
        // whatever the route rows happen to say. An operator locked out by a policy mistake needs this to
        // be true without editing every route first.
        Assert.All(sites, s => Assert.False(s.Protected));
    }

    [Fact]
    public void WithAuthEnabled_OnlyNonPublicRoutesAreProtected() {
        var sites = ProxySiteProjection.Project(
            [Route("public.example.invalid", AccessMode.Public),
             Route("members.example.invalid", AccessMode.Authenticated),
             Route("secret.example.invalid", AccessMode.Restricted)],
            new AuthOptions { Enabled = true });

        Assert.False(Site(sites, "public.example.invalid").Protected);
        Assert.True(Site(sites, "members.example.invalid").Protected);
        Assert.True(Site(sites, "secret.example.invalid").Protected);
    }

    // ── Watchtower routes (ADR-0021) ──────────────────────────────────────────

    [Fact]
    public void AWatchtowerRoute_IsServedByWatchtowerItself_AndIsNeverProtected() {
        var sites = ProxySiteProjection.Project(
            [SelfRoute(SelfHost), Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = true });

        var self = Site(sites, SelfHost);
        Assert.Equal(ProxySiteProjection.SelfAlias, self.UpstreamHost);
        Assert.Equal(ProxySiteProjection.SelfPort, self.UpstreamPort);
        Assert.True(self.Local);
        Assert.True(self.Tls);
        Assert.False(self.OnDemand);
        // Protecting the login page with the gate that redirects to the login page is a closed loop, and
        // Watchtower authenticates its own UI natively (§2.5).
        Assert.False(self.Protected);
        // The rest of the table is untouched.
        Assert.True(Site(sites, "members.example.invalid").Protected);
    }

    /// <summary>
    /// The invariant, restated for the new model: <b>a Watchtower route is never protected.</b> The check
    /// constraint refuses to store one that is not <c>Public</c>, and even handed a row that somehow says
    /// otherwise the projection does not gate it — the closed loop is worth ruling out twice.
    /// </summary>
    [Theory]
    [InlineData(AccessMode.Authenticated)]
    [InlineData(AccessMode.Restricted)]
    public void AWatchtowerRoute_IsUnprotected_WhateverItsAccessModeSays(AccessMode mode) {
        var self = SelfRoute(SelfHost);
        self.AccessMode = mode;

        var site = Assert.Single(ProxySiteProjection.Project([self], new AuthOptions { Enabled = true }));

        Assert.False(site.Protected);
        Assert.True(site.Local);
    }

    [Fact]
    public void WatchtowerRoutes_AreServed_WithAuthDisabledToo() {
        var sites = ProxySiteProjection.Project(
            [SelfRoute(SelfHost), Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = false });

        // Unlike the synthesized login hosts it replaced, a Watchtower route is not part of access
        // control: it is the operator saying "serve this instance here", which is just as true with
        // authentication off — that is how the UI stays reachable on its own hostname.
        Assert.True(Site(sites, SelfHost).Local);
        Assert.False(Site(sites, "members.example.invalid").Protected);
    }

    [Fact]
    public void AWatchtowerRouteOnACustomDomain_KeepsOnDemandTls() {
        var self = SelfRoute("login.customer.invalid");
        self.Kind = DomainKind.Custom;

        var site = Assert.Single(ProxySiteProjection.Project([self], new AuthOptions { Enabled = true }));

        // A realm's login host is very often a domain the customer owns, so the lazy-issuance path has to
        // be available here exactly as it is for an application route.
        Assert.True(site.OnDemand);
    }

    [Fact]
    public void AWatchtowerRouteWithTlsOff_IsProjectedAsPlainHttp() {
        var self = SelfRoute(SelfHost);
        self.TlsEnabled = false;

        var site = Assert.Single(ProxySiteProjection.Project([self], new AuthOptions { Enabled = true }));

        Assert.False(site.Tls);
        Assert.True(site.Local);
    }

    [Fact]
    public void TheConfiguredAuthHost_NoLongerProducesASiteOfItsOwn() {
        var sites = ProxySiteProjection.Project(
            [Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = true, Host = SelfHost });

        // Auth:Host is a redirect address for the operator realm, not a statement that this instance
        // serves that hostname (ADR-0021). Serving it takes a route row.
        Assert.Single(sites);
        Assert.DoesNotContain(sites, s => s.Domain == SelfHost);
    }

    [Fact]
    public void AServiceRouteWithNoStackLoaded_IsSkippedRatherThanThrowing() {
        var orphan = new Route {
            Target = RouteTarget.Service, Domain = "gone.example.invalid", ServiceName = "web",
            ContainerPort = 8080,
        };

        var sites = ProxySiteProjection.Project(
            [orphan, Route("members.example.invalid", AccessMode.Public)], new AuthOptions { Enabled = true });

        // The foreign key cascades, so this should not happen — but a reconcile is not the place to throw
        // about it, and emitting a site with no upstream would be worse than emitting none.
        var site = Assert.Single(sites);
        Assert.Equal("members.example.invalid", site.Domain);
    }

    // ── Service routes ────────────────────────────────────────────────────────

    [Fact]
    public void CustomDomains_KeepOnDemandTls_WhenProtected() {
        var custom = Route("app.customer.invalid", AccessMode.Restricted);
        custom.Kind = DomainKind.Custom;

        var site = Assert.Single(ProxySiteProjection.Project([custom], new AuthOptions { Enabled = true }));

        // Access control and lazy certificate issuance are independent concerns; a customer-owned domain
        // needs both.
        Assert.True(site.OnDemand);
        Assert.True(site.Protected);
    }

    [Fact]
    public void IdentityHeaderMode_FlowsFromTheRouteToTheSite() {
        var route = Route("members.example.invalid", AccessMode.Authenticated);
        route.IdentityHeaderMode = IdentityHeaderMode.AuthRequest;

        var site = Assert.Single(ProxySiteProjection.Project([route], new AuthOptions { Enabled = true }));

        // The proxy config builder reads the mode off the site to decide copy_headers; it originates here.
        Assert.True(site.Protected);
        Assert.Equal(IdentityHeaderMode.AuthRequest, site.Mode);
    }

    // ── Provenance: every site comes from a row now ───────────────────────────

    [Fact]
    public void RouteDerivedSites_CarryRouteIdAndBypassPaths() {
        var route = Route("members.example.invalid", AccessMode.Authenticated);
        route.Id = 42;
        route.BypassPaths = "/webhooks/\n/healthz";

        var site = Assert.Single(ProxySiteProjection.Project([route], new AuthOptions { Enabled = true }));

        // A provider that enforces access control in-process (rather than delegating to forward-auth)
        // needs the originating row and its bypass list; the projection is where they come from.
        Assert.Equal(42, site.RouteId);
        Assert.Equal("/webhooks/\n/healthz", site.BypassPaths);
        Assert.False(site.Local);
    }

    [Fact]
    public void WatchtowerSites_CarryTheirRouteId_AndNoBypassPaths() {
        var self = SelfRoute(SelfHost);
        self.Id = 7;
        // The column is unusable on this kind of row (the handlers never set it), and a bypass list on a
        // site nothing gates would be dead configuration — so it is dropped rather than carried.
        self.BypassPaths = "/webhooks/";

        var site = Assert.Single(ProxySiteProjection.Project([self], new AuthOptions { Enabled = true }));

        // The whole point of the change: a served hostname has a row, so it has a status, a certificate
        // and an audit trail like every other.
        Assert.Equal(7, site.RouteId);
        Assert.Null(site.BypassPaths);
        Assert.Equal(IdentityHeaderMode.None, site.Mode);
    }

    private static ProxySite Site(IEnumerable<ProxySite> sites, string domain) =>
        Assert.Single(sites, s => s.Domain == domain);

    private static Route Route(string domain, AccessMode mode) => new() {
        Target = RouteTarget.Service,
        StackId = 1,
        Domain = domain,
        ServiceName = "web",
        ContainerPort = 8080,
        AccessMode = mode,
        Stack = new Stack {
            Name = "watchtower",
            RepositoryUrl = "https://example.invalid/demo.git",
            ComposeFilePath = "docker-compose.yml",
            Branch = "main",
            ComposeProjectName = "watchtower",
        },
    };

    private static Route SelfRoute(string domain) => new() {
        Target = RouteTarget.Watchtower,
        StackId = null,
        RealmId = Realm.SystemRealmId,
        Domain = domain,
        ServiceName = string.Empty,
        ContainerPort = 0,
        TlsEnabled = true,
        AccessMode = AccessMode.Public,
    };
}
