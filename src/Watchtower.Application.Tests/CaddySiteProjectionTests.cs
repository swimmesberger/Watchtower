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
            new AuthOptions { Enabled = false, Host = AuthHost });

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
            new AuthOptions { Enabled = true });

        Assert.False(Site(sites, "public.example.invalid").Protected);
        Assert.True(Site(sites, "members.example.invalid").Protected);
        Assert.True(Site(sites, "secret.example.invalid").Protected);
    }

    [Fact]
    public void SelfRoute_IsAddedForTheAuthHost_AndIsNeverProtected() {
        var sites = CaddyManager.ProjectSites(
            [Route("members.example.invalid", AccessMode.Authenticated)],
            new AuthOptions { Enabled = true, Host = $"  {AuthHost.ToUpperInvariant()}  " });

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

        var sites = CaddyManager.ProjectSites([explicitRoute], new AuthOptions { Enabled = true, Host = AuthHost });

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
            new AuthOptions { Enabled = true, Host = AuthHost });

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
            new AuthOptions { Enabled = true, Host = null });

        Assert.Single(sites);
    }

    [Fact]
    public void CustomDomains_KeepOnDemandTls_WhenProtected() {
        var custom = Route("app.customer.invalid", AccessMode.Restricted);
        custom.Kind = DomainKind.Custom;

        var site = Assert.Single(CaddyManager.ProjectSites([custom], new AuthOptions { Enabled = true }));

        // Access control and lazy certificate issuance are independent concerns; a customer-owned domain
        // needs both.
        Assert.True(site.OnDemand);
        Assert.True(site.Protected);
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
