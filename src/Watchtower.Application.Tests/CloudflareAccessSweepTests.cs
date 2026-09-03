using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <c>CloudflareTunnelProvider.StaleApps</c> — which Access applications a reconcile deletes.
/// Two things have to hold at once, and they pull in opposite directions: an application the projection
/// still wants must survive every cycle (or the reconcile would delete and recreate it forever), and one
/// nothing wants any more must go (or a route flipped back to Public would stay gated at the edge).
/// </summary>
public sealed class CloudflareAccessSweepTests {
    private static CloudflareAccessApp Existing(string id, string name, string domain) =>
        new() { Id = id, Name = name, Domain = domain, Type = "self_hosted" };

    private static Route NewRoute(int id, string domain, AccessMode mode, string? bypassPaths = null) => new() {
        Id = id,
        Domain = domain,
        ServiceName = "web",
        ContainerPort = 80,
        AccessMode = mode,
        BypassPaths = bypassPaths,
    };

    private static CloudflareTunnelProvider.AccessProjection Project(params Route[] routes) =>
        CloudflareTunnelProvider.ProjectAccessApps(
            routes,
            new Dictionary<int, string[]>(),
            new CloudflareProxyOptions { AccessAllowedEmailDomains = "example.com" });

    [Fact]
    public void AnAppTheProjectionStillWants_IsNotStale() {
        var projection = Project(NewRoute(1, "app.example.com", AccessMode.Authenticated));
        var stale = CloudflareTunnelProvider.StaleApps(
            [Existing("a1", "watchtower: app.example.com", "app.example.com")], projection);

        Assert.Empty(stale);
    }

    /// <summary>
    /// The reason the sweep is keyed on names as well as domains: a bypass app's domain carries a path,
    /// so adding or removing a public path moves it while the name stays. Keyed on domains alone, the
    /// app would be deleted and recreated on every single reconcile.
    /// </summary>
    [Fact]
    public void ABypassAppWhoseFirstPathMoved_IsNotStale() {
        var projection = Project(NewRoute(1, "app.example.com", AccessMode.Authenticated, "/a\n/webhooks"));
        var stale = CloudflareTunnelProvider.StaleApps(
            [Existing("b1", "watchtower: app.example.com (public paths)", "app.example.com/webhooks")],
            projection);

        Assert.Empty(stale);
    }

    /// <summary>Flipping a route to Public removes both of its applications, not just the gate.</summary>
    [Fact]
    public void ARouteTurnedPublic_LeavesBothOfItsAppsStale() {
        var projection = Project(NewRoute(1, "app.example.com", AccessMode.Public, "/webhooks"));
        var stale = CloudflareTunnelProvider.StaleApps(
            [
                Existing("a1", "watchtower: app.example.com", "app.example.com"),
                Existing("b1", "watchtower: app.example.com (public paths)", "app.example.com/webhooks"),
            ],
            projection).ToList();

        Assert.Equal(["a1", "b1"], stale.Select(a => a.Id));
    }

    /// <summary>Removing the last public path removes its app and leaves the route's own alone.</summary>
    [Fact]
    public void RemovingTheLastBypassPath_LeavesOnlyTheBypassAppStale() {
        var projection = Project(NewRoute(1, "app.example.com", AccessMode.Authenticated));
        var stale = CloudflareTunnelProvider.StaleApps(
            [
                Existing("a1", "watchtower: app.example.com", "app.example.com"),
                Existing("b1", "watchtower: app.example.com (public paths)", "app.example.com/webhooks"),
            ],
            projection).ToList();

        var only = Assert.Single(stale);
        Assert.Equal("b1", only.Id);
    }

    /// <summary>
    /// A protected route with no allow source keeps its application — the reconcile turns it into a
    /// deny-all rather than deleting it, which is the whole point of ADR-0035.
    /// </summary>
    [Fact]
    public void ALockedOutRoute_KeepsItsApp() {
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com", AccessMode.Authenticated)],
            new Dictionary<int, string[]>(),
            new CloudflareProxyOptions());
        var stale = CloudflareTunnelProvider.StaleApps(
            [Existing("a1", "watchtower: app.example.com", "app.example.com")], projection);

        Assert.Empty(stale);
    }

    /// <summary>An application somebody made in the dashboard is not ours to remove.</summary>
    [Fact]
    public void AnAppWithoutTheWatchtowerPrefix_IsNeverStale() {
        var projection = Project(NewRoute(1, "app.example.com", AccessMode.Authenticated));
        var stale = CloudflareTunnelProvider.StaleApps(
            [Existing("x1", "internal wiki", "wiki.example.com")], projection);

        Assert.Empty(stale);
    }

    /// <summary>Cloudflare's hostnames are case-insensitive; the names Watchtower gave the apps are not.</summary>
    [Fact]
    public void DomainMatchingIgnoresCase() {
        var projection = Project(NewRoute(1, "app.example.com", AccessMode.Authenticated));
        var stale = CloudflareTunnelProvider.StaleApps(
            [Existing("a1", "watchtower: APP.EXAMPLE.COM", "APP.EXAMPLE.COM")], projection);

        Assert.Empty(stale);
    }
}
