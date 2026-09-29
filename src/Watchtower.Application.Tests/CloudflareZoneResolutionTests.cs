using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The two answers the Cloudflare zone listing is turned into (ADR-0036): which zone a hostname's DNS
/// record belongs in, and which base domains the create form offers. Both are pure, and both are
/// expensive to get wrong in a way nobody sees — a record written into the wrong zone lands somewhere
/// nobody is looking, and a domain offered twice makes the picker read as if two different things were
/// on it.
/// </summary>
public sealed class CloudflareZoneResolutionTests {
    private static CloudflareZone Zone(string id, string name, string status = "active") =>
        new() { Id = id, Name = name, Status = status };

    // ── Which zone a hostname belongs in ─────────────────────────────────────

    /// <summary>
    /// The longest suffix wins, so a delegated sub-zone keeps its own records instead of having them
    /// written into the parent that also matches.
    /// </summary>
    [Fact]
    public void TheMostSpecificZoneWins() {
        var zones = new[] { Zone("parent", "example.com"), Zone("child", "eu.example.com") };

        Assert.Equal("child", CloudflareZoneCatalog.ResolveZoneId(zones, "app.eu.example.com"));
        Assert.Equal("parent", CloudflareZoneCatalog.ResolveZoneId(zones, "app.example.com"));
    }

    /// <summary>The answer is the set's, not the listing order's — Cloudflare's order is its own.</summary>
    [Fact]
    public void TheOrderTheZonesArrivedInDoesNotDecide() {
        var zones = new[] { Zone("child", "eu.example.com"), Zone("parent", "example.com") };

        Assert.Equal("child", CloudflareZoneCatalog.ResolveZoneId(zones, "app.eu.example.com"));
    }

    /// <summary>The apex is a hostname an operator routes like any other.</summary>
    [Fact]
    public void TheZoneApexResolvesToItsOwnZone() =>
        Assert.Equal("z1", CloudflareZoneCatalog.ResolveZoneId([Zone("z1", "example.com")], "example.com"));

    [Fact]
    public void ZoneNamesAreMatchedWithoutRegardToCase() =>
        Assert.Equal("z1", CloudflareZoneCatalog.ResolveZoneId([Zone("z1", "Example.COM")], "APP.example.com"));

    /// <summary>
    /// The boundary has to fall on a label. Writing a stranger's domain into the operator's zone would
    /// fail loudly at best, and at worst succeed.
    /// </summary>
    [Fact]
    public void ADomainThatMerelyEndsWithAZonesNameIsNotInIt() =>
        Assert.Null(CloudflareZoneCatalog.ResolveZoneId([Zone("z1", "example.com")], "notexample.com"));

    [Fact]
    public void NoZoneAtAllIsNoAnswer() =>
        Assert.Null(CloudflareZoneCatalog.ResolveZoneId([], "app.example.com"));

    /// <summary>
    /// The nameless zone the fail-open fallback produces — a configured zone id whose name Cloudflare
    /// would not say — must never claim a hostname. It is still reached, as the caller's own fallback,
    /// which is where it belongs: a guess, not a match.
    /// </summary>
    [Fact]
    public void AZoneWithNoNameMatchesNothing() =>
        Assert.Null(CloudflareZoneCatalog.ResolveZoneId([Zone("z1", "")], "app.example.com"));

    // ── The base domains the client is offered ───────────────────────────────

    [Fact]
    public void MergeOffersBothSourcesAndSaysWhichIsWhich() {
        var merged = PrimaryDomains.Merge(["typed.example"], [Zone("z1", "zone.example")]);

        var typed = Assert.Single(merged, d => d.Name == "typed.example");
        Assert.Equal(PrimaryDomainSources.Configured, typed.Source);
        Assert.Null(typed.ZoneId);
        Assert.Equal("Listed under Settings → Reverse proxy → Primary domains.", typed.Detail);

        var zone = Assert.Single(merged, d => d.Name == "zone.example");
        Assert.Equal(PrimaryDomainSources.CloudflareZone, zone.Source);
        Assert.Equal("z1", zone.ZoneId);
        Assert.Equal("A zone your Cloudflare API token can see.", zone.Detail);
    }

    /// <summary>
    /// Typing a domain into the setting is a statement; a zone listing agreeing with it is not news. The
    /// operator's entry therefore survives, and the domain appears once.
    /// </summary>
    [Fact]
    public void ADomainInBothSourcesAppearsOnceAsTheConfiguredOne() {
        var merged = PrimaryDomains.Merge(["example.com"], [Zone("z1", "EXAMPLE.com")]);

        var only = Assert.Single(merged);
        Assert.Equal("example.com", only.Name);
        Assert.Equal(PrimaryDomainSources.Configured, only.Source);
        Assert.Null(only.ZoneId);
    }

    /// <summary>
    /// Sorted, because Cloudflare's listing order is not stable across calls and a picker that reshuffles
    /// under the cursor is worse than one that is merely not in the operator's own order.
    /// </summary>
    [Fact]
    public void TheMergedListIsSortedByName() {
        var merged = PrimaryDomains.Merge(
            ["zulu.example"], [Zone("z1", "mike.example"), Zone("z2", "alfa.example")]);

        Assert.Equal(["alfa.example", "mike.example", "zulu.example"], merged.Select(d => d.Name));
    }

    /// <summary>A zone with no name is nothing a hostname could be built on, so it is not offered.</summary>
    [Fact]
    public void ANamelessZoneIsNotOffered() {
        var merged = PrimaryDomains.Merge([], [Zone("z1", ""), Zone("z2", "example.com")]);

        Assert.Equal(["example.com"], merged.Select(d => d.Name));
    }

    [Fact]
    public void NothingConfiguredAndNothingDiscoveredIsAnEmptyList() =>
        Assert.Empty(PrimaryDomains.Merge([], []));
}
