using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The host-name rules the whole certificate plane shares. They are strict because both ends are
/// expensive to get wrong: a name a CA refuses costs a rate-limited validation failure, and a name the
/// certificate store would write to disk is a path.
/// </summary>
public sealed class DesiredHostsTests {
    [Theory]
    [InlineData("app.example.com", "app.example.com")]
    [InlineData("  App.Example.COM  ", "app.example.com")]
    [InlineData("app.example.com.", "app.example.com")]                // The fully-qualified form.
    [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")]     // Punycode is plain ASCII.
    [InlineData("a-b-c.d-e.f", "a-b-c.d-e.f")]
    [InlineData("localhost", "localhost")]
    public void AcceptedAndNormalized(string raw, string expected) {
        Assert.True(DesiredHosts.TryNormalize(raw, out var host, out var reason));
        Assert.Equal(expected, host);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData(".", "required")]
    [InlineData("*.example.com", "Wildcard")]
    [InlineData("app .example.com", "spaces")]
    [InlineData("https://app.example.com", "scheme or path")]
    [InlineData("app.example.com/path", "scheme or path")]
    [InlineData("app.example.com:8443", "port")]
    [InlineData("bücher.example", "punycode")]
    [InlineData("app..example.com", "empty label")]
    [InlineData("192.0.2.1", "IP address")]
    [InlineData("[2001:db8::1]", "port")]
    [InlineData("-app.example.com", "cannot start or end with")]
    [InlineData("app-.example.com", "cannot start or end with")]
    [InlineData("app_1.example.com", "letters, digits")]
    public void RejectedWithAReason(string? raw, string expectedFragment) {
        Assert.False(DesiredHosts.TryNormalize(raw, out var host, out var reason));
        Assert.Equal("", host);
        Assert.Contains(expectedFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverLongNamesAndLabelsAreRejected() {
        var longLabel = new string('a', 64);
        Assert.False(DesiredHosts.TryNormalize($"{longLabel}.example.com", out _, out var labelReason));
        Assert.Contains("63", labelReason);

        // 255 characters: over the 253-character limit even after the trailing dot is stripped.
        var longName = string.Join('.', Enumerable.Repeat(new string('a', 50), 5)) + ".example";
        Assert.True(longName.Length > 253);
        Assert.False(DesiredHosts.TryNormalize(longName, out _, out var nameReason));
        Assert.Contains("253", nameReason);
    }

    /// <summary>
    /// The punycode hint is the whole reason non-ASCII is refused rather than converted:
    /// <c>IdnMapping</c> throws under <c>InvariantGlobalization</c>, which the API host builds with.
    /// </summary>
    [Fact]
    public void NonAsciiNamesGetThePunycodeHint() {
        Assert.False(DesiredHosts.TryNormalize("münchen.example", out _, out var reason));
        Assert.Contains("xn--", reason);
    }

    [Fact]
    public void DiffReportsBothSides() {
        var current = new HashSet<string>(["a.test", "b.test"], StringComparer.Ordinal);
        var next = new HashSet<string>(["b.test", "c.test"], StringComparer.Ordinal);

        var diff = DesiredHosts.Diff(current, next);

        Assert.Equal(["c.test"], diff.Added);
        Assert.Equal(["a.test"], diff.Removed);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void AnUnchangedSetDiffsToNothing() {
        var set = new HashSet<string>(["a.test"], StringComparer.Ordinal);
        Assert.True(DesiredHosts.Diff(set, new HashSet<string>(set, StringComparer.Ordinal)).IsEmpty);
    }
}
