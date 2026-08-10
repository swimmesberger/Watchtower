using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <c>CaddyManager.ProjectSites</c> — which routes end up behind access control, and when
/// Watchtower emits a site block for itself (docs/central-auth/design.md §6, §11).
/// </summary>
public sealed class CaddySiteProjectionTests {
    private const string AuthHost = "watchtower.example.invalid";

    [Fact]
    public void WithAuthDisabled_NothingIsProtected_AndThereIsNoSelfRoute() {
        var sites = CaddyManager.ProjectSites(
            [Route("public.example.invalid", AccessMode.Public),
             Route("members.example.invalid", AccessMode.Authenticated),
             Route("secret.example.invalid", AccessMode.Restricted)],
            new AuthOptions { Enabled = false, Host = AuthHost },
            realmAuthHosts: []);

        // The escape hatch: turning access control off restores the previous configuration exactly,
        // whatever the route rows happen to say. An operator locked out by a policy mistake needs this to
        // be true without editing every route first.
        Assert.All(sites, s => Assert.False(s.Protected));
        Assert.DoesNotContain(sites, s => s.Domain == AuthHost);
    }

    [Fact]
    public void WithAuthEnabled_OnlyNonPublicRoutesAreProtected() {
        var sites = CaddyManager.ProjectSites(
            [Route("public.example.invalid", AccessMode.Public),
             Route("members.example.invalid", AccessMode.Authenticated),
             Route("secret.example.invalid", AccessMode.Restricted)],
            new AuthOptions { Enabled = true }, realmAuthHosts: []);

        Assert.False(Site(sites, "public.example.invalid").Protected);
        Assert.True(Site(sites, "members.example.invalid").Protected);
        Assert.True(Site(sites, "secret.example.invalid").Protected);
    }

    [Fact]
    public void SelfRoute_IsAddedForTheAuthHost_AndIsNeverProtected() {
        var sites = CaddyManager.ProjectSites(
            [Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = true, Host = $"  {AuthHost.ToUpperInvariant()}  " },
            realmAuthHosts: []);

        var self = Site(sites, AuthHost);
        Assert.Equal("watchtower", self.UpstreamHost);
        Assert.Equal(8080, self.UpstreamPort);
        Assert.True(self.Tls);
        Assert.False(self.OnDemand);
        // Protecting the login page with the gate that redirects to the login page is a closed loop, and
        // Watchtower authenticates its own UI natively anyway (§2.5).
        Assert.False(self.Protected);
    }

    [Fact]
    public void SelfRoute_DoesNotShadowAnExplicitRouteForTheSameDomain() {
        var explicitRoute = Route(AuthHost, AccessMode.Public);
        explicitRoute.ContainerPort = 3000;

        var sites = CaddyManager.ProjectSites(
            [explicitRoute], new AuthOptions { Enabled = true, Host = AuthHost }, realmAuthHosts: []);

        // The operator has said what that host should do; quietly replacing it would be surprising in the
        // one place surprises are least affordable.
        var site = Assert.Single(sites);
        Assert.Equal(3000, site.UpstreamPort);
        Assert.Equal("watchtower-web", site.UpstreamHost);
    }

    [Fact]
    public void ExplicitAuthHostRoute_RendersButIsNeverProtected_EvenWhenSetNonPublic() {
        // The lockout trap: an operator creates a real Route for the auth host and, meaning well, sets it
        // Authenticated or Restricted. Gating it would put the login page behind forward_auth, which
        // redirects to the login page — the UI would then be reachable only via the published port.
        var authHostRoute = Route(AuthHost, AccessMode.Restricted);

        var sites = CaddyManager.ProjectSites(
            [authHostRoute, Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = true, Host = AuthHost },
            realmAuthHosts: []);

        var self = Site(sites, AuthHost);
        Assert.False(self.Protected);
        // The explicit row still renders (its upstream, not the synthesised self-route's).
        Assert.Equal("watchtower-web", self.UpstreamHost);
        // The other protected route is unaffected.
        Assert.True(Site(sites, "members.example.invalid").Protected);
    }

    [Fact]
    public void WithoutAnAuthHost_NoSelfRouteIsEmitted() {
        var sites = CaddyManager.ProjectSites(
            [Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = true, Host = null }, realmAuthHosts: []);

        Assert.Single(sites);
    }

    // ── Per-realm login hosts (design.md §13) ─────────────────────────────────

    [Fact]
    public void EveryRealmLoginHost_GetsItsOwnUnprotectedSelfRoute() {
        var sites = CaddyManager.ProjectSites(
            [Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = true, Host = AuthHost },
            ["login.acme.invalid", "  LOGIN.Contoso.INVALID  "]);

        // A protected app redirects to its own realm's login page, so that page has to be served — the
        // same bootstrap argument the operator self-route has always answered, now once per population.
        foreach (var host in new[] { AuthHost, "login.acme.invalid", "login.contoso.invalid" }) {
            var self = Site(sites, host);
            Assert.Equal("watchtower", self.UpstreamHost);
            Assert.Equal(8080, self.UpstreamPort);
            Assert.True(self.Tls);
            Assert.False(self.Protected);
        }
        Assert.Equal(4, sites.Count);
    }

    /// <summary>
    /// The invariant, stated as a test: <b>no realm's login host may sit behind its own gate.</b> An
    /// operator who creates an explicit route for one and, meaning well, marks it Authenticated or
    /// Restricted would otherwise put that login page behind the forward-auth that redirects to it.
    /// </summary>
    [Fact]
    public void AnExplicitRouteForARealmLoginHost_RendersButIsForceUnprotected() {
        var realmHostRoute = Route("login.acme.invalid", AccessMode.Restricted);
        realmHostRoute.ContainerPort = 3000;

        var sites = CaddyManager.ProjectSites(
            [realmHostRoute, Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = true, Host = AuthHost },
            ["login.acme.invalid"]);

        var self = Site(sites, "login.acme.invalid");
        Assert.False(self.Protected);
        // The operator's own row still renders — only its access mode is overridden.
        Assert.Equal("watchtower-web", self.UpstreamHost);
        Assert.Equal(3000, self.UpstreamPort);
        // Everything else is untouched, including the operator self-route.
        Assert.True(Site(sites, "members.example.invalid").Protected);
        Assert.False(Site(sites, AuthHost).Protected);
    }

    [Fact]
    public void ARealmSharingTheOperatorLoginHost_ProducesOneSiteBlock() {
        var sites = CaddyManager.ProjectSites(
            [],
            new AuthOptions { Enabled = true, Host = AuthHost },
            [AuthHost.ToUpperInvariant(), "login.acme.invalid", "login.acme.invalid"]);

        // The handlers refuse to store a realm host equal to the configured one, and duplicates cannot
        // exist behind the unique index — but a projection that emitted two blocks for one domain would
        // produce a Caddyfile that does not load, so it collapses them rather than trusting that.
        Assert.Equal(2, sites.Count);
        Assert.Single(sites, s => s.Domain == AuthHost);
        Assert.Single(sites, s => s.Domain == "login.acme.invalid");
    }

    [Fact]
    public void WithAuthDisabled_RealmLoginHostsAreNotServedEither() {
        var sites = CaddyManager.ProjectSites(
            [Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = false, Host = AuthHost },
            ["login.acme.invalid"]);

        // The escape hatch stays total: turning access control off restores exactly the previous
        // configuration, and a realm's login page is part of access control.
        Assert.Single(sites);
        Assert.All(sites, s => Assert.False(s.Protected));
    }

    [Fact]
    public void ARealmWithNoLoginHostYet_ContributesNoSite() {
        var sites = CaddyManager.ProjectSites(
            [], new AuthOptions { Enabled = true, Host = AuthHost }, ["", "   "]);

        // Nothing to serve: such a realm's routes fail closed at challenge time instead.
        Assert.Equal(AuthHost, Assert.Single(sites).Domain);
    }

    [Fact]
    public void CustomDomains_KeepOnDemandTls_WhenProtected() {
        var custom = Route("app.customer.invalid", AccessMode.Restricted);
        custom.Kind = DomainKind.Custom;

        var site = Assert.Single(CaddyManager.ProjectSites([custom], new AuthOptions { Enabled = true }, realmAuthHosts: []));

        // Access control and lazy certificate issuance are independent concerns; a customer-owned domain
        // needs both.
        Assert.True(site.OnDemand);
        Assert.True(site.Protected);
    }

    [Fact]
    public void IdentityHeaderMode_FlowsFromTheRouteToTheSite() {
        var route = Route("members.example.invalid", AccessMode.Authenticated);
        route.IdentityHeaderMode = IdentityHeaderMode.AuthRequest;

        var site = Assert.Single(CaddyManager.ProjectSites([route], new AuthOptions { Enabled = true }, realmAuthHosts: []));

        // The proxy config builder reads the mode off the site to decide copy_headers; it originates here.
        Assert.True(site.Protected);
        Assert.Equal(IdentityHeaderMode.AuthRequest, site.Mode);
    }

    private static CaddySite Site(IEnumerable<CaddySite> sites, string domain) =>
        Assert.Single(sites, s => s.Domain == domain);

    private static Route Route(string domain, AccessMode mode) => new() {
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
}
