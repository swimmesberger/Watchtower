using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <c>CloudflareTunnelProvider.ZoneForDomain</c> — the zone a route's CNAME is written into
/// (ADR-0036). The discovered zones decide, and the configured Zone ID is the fallback; the two guards
/// together are what let an install whose token carries no <c>Zone:Read</c> keep behaving exactly as it
/// did, while one that has it publishes across every zone at once.
/// </summary>
public sealed class CloudflareZoneRoutingTests {
    private static CloudflareZone Zone(string id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void EachDomainGoesToTheZoneThatCoversIt() {
        CloudflareZone[] zones = [Zone("one", "first.example"), Zone("two", "second.example")];

        Assert.Equal("one", CloudflareTunnelProvider.ZoneForDomain(zones, "app.first.example", null));
        Assert.Equal("two", CloudflareTunnelProvider.ZoneForDomain(zones, "app.second.example", null));
    }

    /// <summary>The delegated sub-zone keeps its own records — the same longest-suffix rule as everywhere.</summary>
    [Fact]
    public void TheMostSpecificZoneWins() {
        CloudflareZone[] zones = [Zone("parent", "example.com"), Zone("child", "eu.example.com")];

        Assert.Equal("child", CloudflareTunnelProvider.ZoneForDomain(zones, "app.eu.example.com", "configured"));
    }

    /// <summary>
    /// The pre-ADR-0036 install: nothing could be listed, so the configured zone takes every domain, as
    /// it always did.
    /// </summary>
    [Fact]
    public void NothingDiscovered_FallsBackToTheConfiguredZone() =>
        Assert.Equal("configured", CloudflareTunnelProvider.ZoneForDomain([], "app.example.com", "configured"));

    /// <summary>
    /// A hostname outside every listed zone still gets the configured one. It may well be wrong, and the
    /// upsert will say so in Cloudflare's own words — which is a better outcome than silently not writing
    /// a record for the operator who deliberately named their zone.
    /// </summary>
    [Fact]
    public void ADomainNoListedZoneCovers_FallsBackToTheConfiguredZone() =>
        Assert.Equal(
            "configured",
            CloudflareTunnelProvider.ZoneForDomain([Zone("one", "other.example")], "app.example.com", "configured"));

    /// <summary>Neither source can say, so the route is told rather than having a record written blind.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ADomainNoZoneCoversAndNoConfiguredZone_HasNoAnswer(string? configured) =>
        Assert.Null(CloudflareTunnelProvider.ZoneForDomain(
            [Zone("one", "other.example")], "app.example.com", configured));

    /// <summary>
    /// The configured id is the fallback, never the first answer: preferring it would send every hostname
    /// to one zone, including the ones that demonstrably live elsewhere.
    /// </summary>
    [Fact]
    public void ADiscoveredZoneBeatsTheConfiguredOne() =>
        Assert.Equal(
            "discovered",
            CloudflareTunnelProvider.ZoneForDomain(
                [Zone("discovered", "example.com")], "app.example.com", "configured"));

    /// <summary>Stated once, so the reconcile, the deletion path and this test read the same sentence.</summary>
    [Fact]
    public void TheUncoveredDomainMessageNamesTheDomainAndBothRemedies() {
        var message = CloudflareTunnelProvider.NoZoneCovers("app.example.com");

        Assert.Contains("app.example.com", message, StringComparison.Ordinal);
        Assert.Contains("Zone:Read", message, StringComparison.Ordinal);
        Assert.Contains("Settings → Reverse proxy", message, StringComparison.Ordinal);
    }
}
