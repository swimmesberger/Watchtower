using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <c>CloudflareTunnelProvider.ProjectAccessApps</c> — which routes get a Zero Trust Access
/// application and who its allow policy admits. The skip rule is the security-relevant part in both
/// directions: an empty allow-list must not publish a deny-all app (silent total lockout), and a
/// skipped route must not count as "unprotected" for the deletion sweep.
/// </summary>
public sealed class CloudflareAccessProjectionTests {
    private static Route NewRoute(int id, string domain, AccessMode mode) => new() {
        Id = id,
        Domain = domain,
        ServiceName = "web",
        ContainerPort = 80,
        AccessMode = mode,
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

    [Fact]
    public void EmptyAllowList_SkipsWithAWarning_InsteadOfPublishingDenyAll() {
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [
                NewRoute(1, "auth.example.com", AccessMode.Authenticated),
                NewRoute(2, "granted.example.com", AccessMode.Restricted),
            ],
            new Dictionary<int, string[]>(),
            NoDefaults);

        Assert.Empty(projection.Apps);
        Assert.Equal(2, projection.Warnings.Count);
        Assert.Contains(projection.Warnings, w => w.Contains("auth.example.com"));
        Assert.Contains(projection.Warnings, w => w.Contains("granted.example.com"));
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
