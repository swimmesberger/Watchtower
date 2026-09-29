using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <c>CloudflareTunnelProvider.ProjectAccessApps</c> — which routes get a Zero Trust Access
/// application, what its policy decides, and which of them get a second application for the paths that
/// stay public. The lockout rule is the security-relevant part: since new routes are protected by
/// default (ADR-0035), a protected route whose allow-list is empty must publish a deny-all rather than
/// be skipped, or an operator who believes a hostname is gated is serving it to the internet.
/// </summary>
public sealed class CloudflareAccessProjectionTests {
    private static Route NewRoute(int id, string domain, AccessMode mode, string? bypassPaths = null) => new() {
        Id = id,
        Domain = domain,
        ServiceName = "web",
        ContainerPort = 80,
        AccessMode = mode,
        BypassPaths = bypassPaths,
    };

    private static readonly CloudflareProxyOptions NoDefaults = new();

    [Fact]
    public void PublicRoutes_GetNoAccessApp() {
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "open.example.com", AccessMode.Public)],
            new Dictionary<int, string[]>(),
            NoDefaults);
        Assert.Empty(projection.Apps);
        Assert.Empty(projection.Warnings);
    }

    [Fact]
    public void AuthenticatedRoutes_AdmitTheConfiguredEmailsAndDomains() {
        var cf = new CloudflareProxyOptions {
            AccessAllowedEmails = "ops@example.com, admin@example.com",
            AccessAllowedEmailDomains = "example.com",
        };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com", AccessMode.Authenticated)],
            new Dictionary<int, string[]>(),
            cf);

        var app = Assert.Single(projection.Apps);
        Assert.Equal("watchtower: app.example.com", app.Name);
        Assert.Equal(["admin@example.com", "ops@example.com"], app.Emails);
        Assert.Equal(["example.com"], app.EmailDomains);
    }

    [Fact]
    public void RestrictedRoutes_AdmitExactlyTheGrantEmails_NotTheInstanceDefaults() {
        var cf = new CloudflareProxyOptions { AccessAllowedEmails = "everyone@example.com" };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(7, "secret.example.com", AccessMode.Restricted)],
            new Dictionary<int, string[]> { [7] = ["alice@example.com"] },
            cf);

        var app = Assert.Single(projection.Apps);
        // Restricted means "only these subjects" — the instance-wide list must not widen it.
        Assert.Equal(["alice@example.com"], app.Emails);
        Assert.Empty(app.EmailDomains);
    }

    /// <summary>
    /// Both flavours of "nobody could pass this": an Authenticated route with nothing configured
    /// instance-wide, and a Restricted one whose grants name nobody with an email. Each gets a deny-all app,
    /// so the hostname is closed rather than quietly open, and each keeps its warning.
    /// </summary>
    [Fact]
    public void EmptyAllowList_PublishesDenyAll_RatherThanLeavingTheRouteOpen() {
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [
                NewRoute(1, "auth.example.com", AccessMode.Authenticated),
                NewRoute(2, "granted.example.com", AccessMode.Restricted),
            ],
            new Dictionary<int, string[]>(),
            NoDefaults);

        Assert.Equal(2, projection.Apps.Count);
        Assert.All(projection.Apps, app => {
            Assert.Equal(CloudflareTunnelProvider.AccessDecisionDeny, app.Decision);
            Assert.True(app.IsLockout);
            Assert.False(app.HasInlineRules);
        });
        Assert.Equal([1, 2], projection.Apps.Select(a => a.RouteId));

        Assert.Equal(2, projection.Warnings.Count);
        Assert.Contains(projection.Warnings, w => w.Contains("auth.example.com"));
        Assert.Contains(projection.Warnings, w => w.Contains("granted.example.com"));
        Assert.All(projection.Warnings, w => Assert.Contains("denying everyone", w, StringComparison.Ordinal));
    }

    /// <summary>An app that admits somebody is an allow, and says nothing about lockouts.</summary>
    [Fact]
    public void AnAllowListedRoute_IsAnAllowApp_AndNoLockout() {
        var cf = new CloudflareProxyOptions { AccessAllowedEmailDomains = "example.com" };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com", AccessMode.Authenticated)],
            new Dictionary<int, string[]>(),
            cf);

        var app = Assert.Single(projection.Apps);
        Assert.Equal(CloudflareTunnelProvider.AccessDecisionAllow, app.Decision);
        Assert.False(app.IsLockout);
        Assert.Null(app.Destinations);
        Assert.Empty(projection.Warnings);
    }

    // ── Bypass applications (the public paths of a protected route) ───────────

    /// <summary>
    /// The second app names every public path of the route; the first path is its primary hostname and
    /// the rest ride along as destinations, because an application has one domain and many destinations.
    /// </summary>
    [Fact]
    public void AProtectedRouteWithBypassPaths_GetsASecondBypassApp() {
        var cf = new CloudflareProxyOptions { AccessAllowedEmailDomains = "example.com" };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com", AccessMode.Authenticated, "/webhooks/\n/healthz")],
            new Dictionary<int, string[]>(),
            cf);

        Assert.Equal(2, projection.Apps.Count);
        var bypass = projection.Apps[1];
        Assert.Equal(CloudflareTunnelProvider.AccessDecisionBypass, bypass.Decision);
        Assert.Equal("watchtower: app.example.com (public paths)", bypass.Name);
        Assert.Equal("app.example.com/healthz", bypass.Domain);
        Assert.Equal(["app.example.com/healthz", "app.example.com/webhooks"], bypass.Destinations!);
        Assert.Equal(1, bypass.RouteId);
        Assert.False(bypass.HasInlineRules);
    }

    /// <summary>
    /// Cloudflare matches path segments, so <c>/webhooks</c> and <c>/webhooks/</c> are one destination —
    /// and a line that is nothing but slashes would name the hostname itself, colliding with the route's
    /// own app, so it is dropped.
    /// </summary>
    [Fact]
    public void BypassPaths_AreStrippedOfTrailingSlashes_Deduplicated_AndSorted() {
        var cf = new CloudflareProxyOptions { AccessAllowedEmailDomains = "example.com" };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com", AccessMode.Authenticated, "/b/\n/a\n/b\n/\n/a/")],
            new Dictionary<int, string[]>(),
            cf);

        var bypass = projection.Apps[1];
        Assert.Equal(["app.example.com/a", "app.example.com/b"], bypass.Destinations!);
    }

    /// <summary>
    /// A public webhook has to keep working while the operator sorts the allow list out, so the deny
    /// route still gets its bypass app — Cloudflare applies the most specific application.
    /// </summary>
    [Fact]
    public void ADenyAllRoute_StillGetsItsBypassApp() {
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com", AccessMode.Authenticated, "/webhooks/")],
            new Dictionary<int, string[]>(),
            NoDefaults);

        Assert.Equal(
            [CloudflareTunnelProvider.AccessDecisionDeny, CloudflareTunnelProvider.AccessDecisionBypass],
            projection.Apps.Select(a => a.Decision));
    }

    /// <summary>A Public route has no access control for anything to be excepted from.</summary>
    [Fact]
    public void APublicRouteWithBypassPaths_GetsNoAppAtAll() {
        var cf = new CloudflareProxyOptions { AccessAllowedEmailDomains = "example.com" };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "open.example.com", AccessMode.Public, "/webhooks/")],
            new Dictionary<int, string[]>(),
            cf);

        Assert.Empty(projection.Apps);
    }

    /// <summary>
    /// Every projected name carries the prefix, because that prefix is the only thing keeping the
    /// deletion sweep away from applications somebody made in the dashboard.
    /// </summary>
    [Fact]
    public void EveryProjectedName_CarriesTheWatchtowerPrefix() {
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [
                NewRoute(1, "auth.example.com", AccessMode.Authenticated, "/webhooks/"),
                NewRoute(2, "granted.example.com", AccessMode.Restricted),
            ],
            new Dictionary<int, string[]>(),
            NoDefaults);

        Assert.Equal(3, projection.Apps.Count);
        Assert.All(projection.Apps, app => Assert.StartsWith("watchtower: ", app.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void AuthenticatedRoutes_CanAdmitAnAccessGroup_TheEntraWorkflow() {
        // The "main user group" workflow: the allow-list lives in a Cloudflare Access group (e.g.
        // Entra ID users); Watchtower references it instead of maintaining a parallel email list.
        var cf = new CloudflareProxyOptions { AccessGroupIds = "grp-1, grp-2" };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com", AccessMode.Authenticated)],
            new Dictionary<int, string[]>(),
            cf);

        var app = Assert.Single(projection.Apps);
        Assert.Equal(["grp-1", "grp-2"], app.GroupIds);
        Assert.Empty(app.Emails);
        Assert.True(app.HasInlineRules);
    }

    [Fact]
    public void ReusablePolicyAlone_IsAValidAllowSource_WithNoInlinePolicy() {
        // A dashboard-maintained default policy attached by id: the app is created, but no
        // Watchtower-generated app-scoped policy is needed (HasInlineRules drives that).
        var cf = new CloudflareProxyOptions { AccessReusablePolicyIds = "pol-1" };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com", AccessMode.Authenticated)],
            new Dictionary<int, string[]>(),
            cf);

        var app = Assert.Single(projection.Apps);
        Assert.Equal(["pol-1"], app.ReusablePolicyIds);
        Assert.False(app.HasInlineRules);
        Assert.Empty(projection.Warnings);
    }

    [Fact]
    public void RestrictedRoutes_AreNotWidenedByGroupsOrReusablePolicies() {
        var cf = new CloudflareProxyOptions { AccessGroupIds = "grp-1", AccessReusablePolicyIds = "pol-1" };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(7, "secret.example.com", AccessMode.Restricted)],
            new Dictionary<int, string[]> { [7] = ["alice@example.com"] },
            cf);

        var app = Assert.Single(projection.Apps);
        Assert.Equal(["alice@example.com"], app.Emails);
        Assert.Empty(app.GroupIds);
        Assert.Empty(app.ReusablePolicyIds);
    }

    [Fact]
    public void AppsAreOrderedByDomain_ForAStableReconcile() {
        var cf = new CloudflareProxyOptions { AccessAllowedEmailDomains = "example.com" };
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [
                NewRoute(2, "b.example.com", AccessMode.Authenticated),
                NewRoute(1, "a.example.com", AccessMode.Authenticated),
            ],
            new Dictionary<int, string[]>(),
            cf);
        Assert.Equal(["a.example.com", "b.example.com"], projection.Apps.Select(a => a.Domain));
    }

    [Fact]
    public void SplitList_HandlesSeparatorsAndDuplicates() {
        Assert.Equal(
            ["a@x.com", "b@x.com"],
            CloudflareProxyOptions.SplitList("a@x.com, b@x.com; A@X.COM\n"));
        Assert.Empty(CloudflareProxyOptions.SplitList(null));
        Assert.Empty(CloudflareProxyOptions.SplitList("  "));
    }
}
