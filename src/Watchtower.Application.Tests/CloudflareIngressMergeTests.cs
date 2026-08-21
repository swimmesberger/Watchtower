using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the merge-don't-replace contract of the tunnel configuration push and the import
/// suggestion heuristic. The merge is the load-bearing part: the configurations endpoint is a
/// whole-config PUT, so a fresh Watchtower pointed at a pre-existing tunnel would otherwise wipe
/// every dashboard-made public hostname on its first reconcile.
/// </summary>
public sealed class CloudflareIngressMergeTests {
    private static CloudflareIngressRule Rule(string? hostname, string service, string? path = null) =>
        new() { Hostname = hostname, Service = service, Path = path };

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

    // ── Merge ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ForeignHostnames_SurviveThePush_VerbatimAndFirst() {
        var existing = new[] {
            Rule("legacy.example.com", "http://192.168.1.50:8080"),
            Rule("nas.example.com", "http://localhost:5000", path: "/admin"),
            Rule(null, "http_status:404"),
        };
        var routes = new[] { NewRoute("app.example.com", "shop", "web", 3000) };

        var merged = CloudflareTunnelProvider.MergeIngress(
            existing, CloudflareTunnelProvider.ProjectIngress(routes), routes.Select(r => r.Domain));

        // Foreign rules first (dashboard order, path intact), then ours, then exactly one catch-all.
        Assert.Equal(
            ["legacy.example.com", "nas.example.com", "app.example.com", null],
            merged.Select(r => r.Hostname));
        Assert.Equal("/admin", merged[1].Path);
        Assert.Equal("http://192.168.1.50:8080", merged[0].Service);
        Assert.Equal("http_status:404", merged[^1].Service);
        Assert.Equal(1, merged.Count(r => r.Hostname is null));
    }

    [Fact]
    public void AHostnameTheRouteTableOwns_IsReplacedByTheProjection_NotDuplicated() {
        var existing = new[] {
            Rule("app.example.com", "http://old-target:9999"),
            Rule(null, "http_status:404"),
        };
        var routes = new[] { NewRoute("app.example.com", "shop", "web", 3000) };

        var merged = CloudflareTunnelProvider.MergeIngress(
            existing, CloudflareTunnelProvider.ProjectIngress(routes), routes.Select(r => r.Domain));

        var rule = Assert.Single(merged, r => r.Hostname == "app.example.com");
        Assert.Equal("http://shop-web:3000", rule.Service);
    }

    [Fact]
    public void EmptyRemoteConfiguration_MergesToJustTheProjection() {
        var routes = new[] { NewRoute("app.example.com", "shop", "web", 3000) };
        var merged = CloudflareTunnelProvider.MergeIngress(
            [], CloudflareTunnelProvider.ProjectIngress(routes), routes.Select(r => r.Domain));
        Assert.Equal(["app.example.com", null], merged.Select(r => r.Hostname));
    }

    [Fact]
    public void ForeignRules_AreExactlyTheUnownedHostnameRules() {
        var existing = new[] {
            Rule("owned.example.com", "http://x:1"),
            Rule("foreign.example.com", "http://y:2"),
            Rule(null, "http_status:404"),
        };
        var foreign = CloudflareTunnelProvider.ForeignIngressRules(existing, ["OWNED.example.com"]);
        var rule = Assert.Single(foreign);
        // Case-insensitive ownership, and the catch-all is never foreign.
        Assert.Equal("foreign.example.com", rule.Hostname);
    }

    // ── Import suggestions ────────────────────────────────────────────────────

    private static readonly List<ListCloudflareForeignRoutes.StackCandidate> Stacks = [
        new(1, "Shop", "shop"),
        new(2, "Shop Staging", "shop-staging"),
    ];

    [Fact]
    public void WatchtowerAliasConvention_IsRecognized() {
        var suggestion = ListCloudflareForeignRoutes.Suggest("http://shop-web:3000", Stacks);
        Assert.NotNull(suggestion);
        Assert.Equal((1, "web", 3000), (suggestion.StackId, suggestion.ServiceName, suggestion.ContainerPort));
    }

    [Fact]
    public void LongestProjectPrefixWins_WhenProjectNamesNest() {
        // "shop-staging-api" matches both "shop" (service "staging-api") and "shop-staging" (service
        // "api"); the longer project is the correct parse of the alias Watchtower itself would write.
        var suggestion = ListCloudflareForeignRoutes.Suggest("http://shop-staging-api:8080", Stacks);
        Assert.NotNull(suggestion);
        Assert.Equal((2, "api", 8080), (suggestion.StackId, suggestion.ServiceName, suggestion.ContainerPort));
    }

    [Fact]
    public void DefaultPorts_AreFilledInFromTheScheme() {
        var suggestion = ListCloudflareForeignRoutes.Suggest("http://shop-web", Stacks);
        Assert.Equal(80, suggestion?.ContainerPort);
    }

    [Theory]
    [InlineData("http://192.168.1.50:8080")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://unrelated-host:80")]
    [InlineData("ssh://shop-web:22")]
    [InlineData("http_status:404")]
    public void AnythingOutsideTheAliasConvention_GetsNoSuggestion(string service) =>
        Assert.Null(ListCloudflareForeignRoutes.Suggest(service, Stacks));
}
