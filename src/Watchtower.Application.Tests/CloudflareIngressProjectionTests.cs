using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <c>CloudflareTunnelProvider.ProjectIngress</c> — the pure projection from the route table
/// onto tunnel ingress rules. The catch-all is the security-relevant part: without a terminal 404
/// rule, Cloudflare routes unmatched hostnames to the last service, quietly exposing an arbitrary
/// container on any hostname pointed at the tunnel.
/// </summary>
public sealed class CloudflareIngressProjectionTests {
    private static Route NewRoute(string domain, string project, string service, int port) => new() {
        Domain = domain,
        ServiceName = service,
        ContainerPort = port,
        Stack = new Stack {
            Name = project,
            ComposeProjectName = project,
            RepositoryUrl = "https://example.com/repo.git",
            ComposeFilePath = "docker-compose.yml",
            Branch = "main",
        },
    };

    [Fact]
    public void ProjectsRoutesOntoHostnameRules_WithPrivateHttpUpstreams() {
        var rules = CloudflareTunnelProvider.ProjectIngress([
            NewRoute("app.example.com", "shop", "web", 3000),
        ]);

        Assert.Equal(2, rules.Count);
        Assert.Equal("app.example.com", rules[0].Hostname);
        // Plain HTTP inside the private ingress network — TLS terminates at Cloudflare's edge.
        Assert.Equal("http://shop-web:3000", rules[0].Service);
    }

    [Fact]
    public void TheLastRuleIsAlwaysTheCatchAll404() {
        var rules = CloudflareTunnelProvider.ProjectIngress([
            NewRoute("b.example.com", "p1", "web", 80),
            NewRoute("a.example.com", "p2", "api", 8080),
        ]);

        Assert.Null(rules[^1].Hostname);
        Assert.Equal("http_status:404", rules[^1].Service);
        // Hostname rules are sorted for a stable configuration (idempotent PUTs).
        Assert.Equal(["a.example.com", "b.example.com"], rules.Take(2).Select(r => r.Hostname));
    }

    [Fact]
    public void EmptyRouteTable_ProjectsOnlyTheCatchAll() {
        var rules = CloudflareTunnelProvider.ProjectIngress([]);
        var rule = Assert.Single(rules);
        Assert.Null(rule.Hostname);
        Assert.Equal("http_status:404", rule.Service);
    }

    [Fact]
    public void RoutesWithoutALoadedStack_AreSkippedNotBroken() {
        var orphan = new Route { Domain = "x.example.com", ServiceName = "web", ContainerPort = 80 };
        var rules = CloudflareTunnelProvider.ProjectIngress([orphan]);
        var rule = Assert.Single(rules);
        Assert.Equal("http_status:404", rule.Service);
    }

    /// <summary>
    /// A Watchtower route (ADR-0023) is not something this provider can serve: an ingress rule pointing at
    /// Watchtower would publish the management plane through the tunnel with no gate in front of it, which
    /// is precisely what Cloudflare Access exists to do properly. The reconcile marks such a route
    /// <c>Error</c> and says so; the projection simply never emits a rule for it.
    /// </summary>
    [Fact]
    public void WatchtowerRoutes_AreNotPublishedThroughTheTunnel() {
        var self = new Route {
            Target = RouteTarget.Watchtower,
            RealmId = Realm.SystemRealmId,
            Domain = "ui.example.com",
            ServiceName = string.Empty,
        };

        var rules = CloudflareTunnelProvider.ProjectIngress([
            self, NewRoute("app.example.com", "shop", "web", 3000),
        ]);

        Assert.Equal(["app.example.com", null], rules.Select(r => r.Hostname));
    }
}
