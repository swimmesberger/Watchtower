using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The suffix arithmetic every primary-domain feature shares — ADR-0036. Each rule here is load-bearing
/// somewhere the mistake would be expensive and late: the parse decides whether a saved setting names
/// any domains at all, coverage decides which routes are filed under a stranger's domain, and
/// <see cref="PrimaryDomains.BestMatch"/> picks the DNS zone a record is written into.
/// </summary>
public sealed class PrimaryDomainsTests {
    [Fact]
    public void ParseNormalizesDeduplicatesAndKeepsTheWrittenOrder() {
        Assert.True(PrimaryDomains.TryParse(
            "wimmesberger.dev, EXAMPLE.COM\nexample.com.", out var domains, out var reason));

        Assert.Equal(["wimmesberger.dev", "example.com"], domains);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , \n ")]
    public void AnEmptySettingNamesNoDomainsAndIsNotAnError(string? raw) {
        Assert.True(PrimaryDomains.TryParse(raw, out var domains, out var reason));
        Assert.Empty(domains);
        Assert.Null(reason);
    }

    /// <summary>
    /// The per-entry rules are <c>DesiredHosts.TryNormalize</c>'s, deliberately — a primary domain is a
    /// thing host names are built on, so anything that could not carry a route is not one. In particular
    /// an IP address is refused here where the LAN-names parser accepts it.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.10", "IP address")]
    [InlineData("*.example.com", "Wildcard")]
    [InlineData("exämple.com", "xn--")]
    [InlineData("example.com:8443", "port")]
    public void JunkEntriesFailTheWholeParse(string raw, string expectedFragment) {
        Assert.False(PrimaryDomains.TryParse(raw, out var domains, out var reason));
        Assert.Empty(domains);
        Assert.Contains(expectedFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFailedParseNamesTheOffendingEntry() {
        Assert.False(PrimaryDomains.TryParse("a.example.com, bad_host", out var domains, out var reason));

        Assert.Empty(domains);
        Assert.Contains("bad_host", reason);
        // Named once, not twice: several of TryNormalize's sentences already quote the entry.
        Assert.Equal(1, reason!.Split("bad_host").Length - 1);
    }

    [Fact]
    public void ABadEntryFailsEvenAfterGoodOnes() {
        Assert.False(PrimaryDomains.TryParse("a.example.com\n192.168.1.10", out var domains, out var reason));
        Assert.Empty(domains);
        Assert.Contains("'192.168.1.10'", reason);
    }

    [Theory]
    [InlineData("example.com", "example.com", true)]           // The apex is covered by itself.
    [InlineData("example.com", "app.example.com", true)]
    [InlineData("example.com", "a.b.example.com", true)]
    [InlineData("example.com", "APP.Example.COM", true)]
    [InlineData("example.com", "notexample.com", false)]       // The boundary has to fall on a label.
    [InlineData("example.com", "example.com.evil.test", false)]
    [InlineData("example.com", "other.test", false)]
    [InlineData("example.com", ".example.com", false)]
    public void CoverageFallsOnLabelBoundaries(string primary, string host, bool covered) =>
        Assert.Equal(covered, PrimaryDomains.Covers(primary, host));

    /// <summary>
    /// Asserted under both orderings because the callers assemble their candidates from a setting and a
    /// provider's zone listing, and neither ordering means anything.
    /// </summary>
    [Theory]
    [InlineData("app.eu.example.com", "eu.example.com")]
    [InlineData("app.example.com", "example.com")]
    [InlineData("eu.example.com", "eu.example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("other.test", null)]
    public void TheLongestCoveringDomainWinsWhateverTheOrder(string host, string? expected) {
        Assert.Equal(expected, PrimaryDomains.BestMatch(["example.com", "eu.example.com"], host));
        Assert.Equal(expected, PrimaryDomains.BestMatch(["eu.example.com", "example.com"], host));
    }

    [Fact]
    public void NoDomainsMeansNoMatch() =>
        Assert.Null(PrimaryDomains.BestMatch([], "app.example.com"));

    [Theory]
    [InlineData("example.com", "example.com", "")]             // The apex, which is a route like any other.
    [InlineData("example.com", "app.example.com", "app")]
    [InlineData("example.com", "a.b.example.com", "a.b")]
    [InlineData("example.com", "notexample.com", null)]
    [InlineData("example.com", "other.test", null)]
    public void SubdomainReportsTheLeadingLabels(string primary, string host, string? expected) =>
        Assert.Equal(expected, PrimaryDomains.Subdomain(primary, host));

    [Theory]
    [InlineData("", "example.com")]
    [InlineData(null, "example.com")]
    [InlineData("   ", "example.com")]
    [InlineData("app", "app.example.com")]
    [InlineData(" .app. ", "app.example.com")]
    [InlineData("a.b", "a.b.example.com")]
    public void ComposeJoinsTheSubdomainOnAndForgivesStrayDots(string? subdomain, string expected) =>
        Assert.Equal(expected, PrimaryDomains.Compose(subdomain, "example.com"));

    [Theory]
    [InlineData("")]
    [InlineData("app")]
    [InlineData("a.b")]
    public void ComposeAndSubdomainRoundTrip(string subdomain) =>
        Assert.Equal(subdomain, PrimaryDomains.Subdomain(
            "example.com", PrimaryDomains.Compose(subdomain, "example.com")));
}
