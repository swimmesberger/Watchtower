using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the pure decisions in <see cref="RouteAccessPolicy"/>: what counts as a host, what counts as an
/// exempt path, and what counts as a usable <c>redirect_uri</c>. Each of these is reached directly by
/// attacker-supplied input, and each fails closed.
/// </summary>
public sealed class RouteAccessPolicyTests {
    [Theory]
    [InlineData("app.example.com", "app.example.com")]
    [InlineData("APP.Example.COM", "app.example.com")]
    [InlineData("app.example.com:8443", "app.example.com")]
    // Caddy sends one value, but a chain of proxies appends; the first entry is the one that named the app.
    [InlineData("app.example.com, evil.example.net", "app.example.com")]
    [InlineData("  app.example.com  ", "app.example.com")]
    public void ForwardedHost_IsNormalizedToTheStoredForm(string header, string expected) =>
        Assert.Equal(expected, RouteAccessPolicy.NormalizeForwardedHost(header));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Anything that is a URL, a credential, or an attempt to have the parser read it as one.
    [InlineData("app.example.com/../other")]
    [InlineData("https://app.example.com")]
    [InlineData("user@app.example.com")]
    [InlineData("app.example.com\\evil.example.net")]
    [InlineData("app.example.com?x=1")]
    public void ForwardedHost_RejectsAnythingThatIsNotAPlainHost(string? header) =>
        Assert.Null(RouteAccessPolicy.NormalizeForwardedHost(header));

    [Theory]
    [InlineData("/reports", "/reports")]
    [InlineData("/reports?range=30d", "/reports")]
    [InlineData("/reports#top", "/reports")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void Path_IsTakenWithoutTheQueryString(string? forwardedUri, string expected) =>
        Assert.Equal(expected, RouteAccessPolicy.ExtractPath(forwardedUri));

    [Fact]
    public void BypassPaths_IgnoreBlanksAndAnythingNotRooted() {
        const string configured = "/webhooks/\n\n  /healthz  \r\nnot-a-path\nhttps://elsewhere.example\n";

        Assert.Equal(["/webhooks/", "/healthz"], RouteAccessPolicy.ParseBypassPaths(configured));
    }

    [Theory]
    [InlineData("/webhooks/github", true)]
    [InlineData("/healthz", true)]
    // Prefix, not segment: "/healthz-internal" matching is the documented behaviour of a prefix list.
    [InlineData("/healthz-internal", true)]
    [InlineData("/reports", false)]
    [InlineData("/", false)]
    public void ExemptPaths_MatchByPrefix(string path, bool expected) =>
        Assert.Equal(expected, RouteAccessPolicy.IsExemptPath("/webhooks/\n/healthz", path));

    [Fact]
    public void ReservedPrefix_IsExemptOnEveryRoute() {
        Assert.True(RouteAccessPolicy.IsExemptPath(bypassPaths: null, "/.watchtower/callback"));
        Assert.True(RouteAccessPolicy.IsExemptPath(bypassPaths: null, "/.watchtower/logout"));
        // The prefix is a directory, not a substring: an app path that merely starts with the same letters
        // is still the app's, and still checked.
        Assert.False(RouteAccessPolicy.IsExemptPath(bypassPaths: null, "/.watchtowerish"));
    }

    [Theory]
    // The reason the guard exists: Caddy matches on the cleaned path but forwards the original, so an
    // exemption decided on the raw string would be handing out access to wherever it normalises to.
    [InlineData("/webhooks/../admin")]
    [InlineData("/.watchtower/../admin")]
    [InlineData("/webhooks/%2e%2e/admin")]
    [InlineData("/webhooks/%2E%2E/admin")]
    [InlineData("/webhooks/./sneaky")]
    public void PathsWithDotSegments_AreNeverExempt(string path) =>
        Assert.False(RouteAccessPolicy.IsExemptPath("/webhooks/", path));

    [Theory]
    [InlineData("https://app.example.com/reports?range=30d")]
    [InlineData("https://app.example.com/")]
    public void RedirectUri_AcceptsAnAbsoluteHttpsAppUrl(string candidate) =>
        Assert.NotNull(RouteAccessPolicy.ParseAppRedirectUri(candidate));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // Relative: there is no app to hand over to.
    [InlineData("/reports")]
    // Plain HTTP would let the app session cookie be established over a channel that cannot carry it.
    [InlineData("http://app.example.com/")]
    [InlineData("javascript:alert(1)")]
    // Userinfo and an explicit port both widen what "the app's own domain" means to a reader.
    [InlineData("https://app.example.com@evil.example.net/")]
    [InlineData("https://app.example.com:8443/")]
    public void RedirectUri_RejectsAnythingElse(string? candidate) =>
        Assert.Null(RouteAccessPolicy.ParseAppRedirectUri(candidate));

    [Fact]
    public void StrippedAndCopiedHeaders_AreTheSameList() {
        // The generated Caddy config strips this list and copies this list. A name copied but not stripped
        // would be client-spoofable, which is the failure mode §2.3 exists to prevent.
        Assert.Equal(
            [RouteAccessPolicy.UserHeaderName, RouteAccessPolicy.EmailHeaderName, RouteAccessPolicy.JwtHeaderName],
            RouteAccessPolicy.IdentityHeaderNames);
    }
}
