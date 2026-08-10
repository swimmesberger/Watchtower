using Watchtower.Application.Entities;
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
    [InlineData("/webhooks/./sneaky")]
    public void PathsWithLiteralDotSegments_AreNeverExempt(string path) =>
        Assert.False(RouteAccessPolicy.IsExemptPath("/webhooks/", path));

    [Theory]
    // The nastier variant: the dots stay literal `..` while the SEPARATOR is encoded, so no segment
    // equals ".." — yet an upstream that decodes %2f→/ (or %5c→\) and normalises reaches /admin. Any
    // percent-encoding at all in the matched path disqualifies the fast-path exemption, because Watchtower
    // does not decode and cannot predict what the upstream will.
    [InlineData("/webhooks/..%2fadmin")]
    [InlineData("/webhooks/..%2Fadmin")]
    [InlineData("/webhooks/..%5cadmin")]
    [InlineData("/webhooks/..%5Cadmin")]
    [InlineData("/webhooks/%2e%2e/admin")]
    [InlineData("/webhooks/%2E%2E/admin")]
    // Even a single encoded byte anywhere in the path — there is no benign reason to encode part of a
    // literal ASCII prefix, so its presence is enough to fall through to the full check.
    [InlineData("/webhooks%2f../admin")]
    [InlineData("/webhooks/legit%20name")]
    public void PathsWithAnyPercentEncoding_AreNeverExempt(string path) =>
        Assert.False(RouteAccessPolicy.IsExemptPath("/webhooks/", path));

    [Theory]
    // The other half of the rule: a plain, un-encoded request to the prefix still matches. A legitimate
    // caller of a bypass path has no need to encode anything, so nothing is lost by disqualifying encoding.
    [InlineData("/webhooks/github")]
    [InlineData("/webhooks/")]
    public void PlainPaths_StillMatchTheBypassPrefix(string path) =>
        Assert.True(RouteAccessPolicy.IsExemptPath("/webhooks/", path));

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

    [Theory]
    [InlineData(IdentityHeaderMode.None)]
    [InlineData(IdentityHeaderMode.Remote)]
    [InlineData(IdentityHeaderMode.AuthRequest)]
    public void EveryCopiedHeader_IsAlsoStripped(IdentityHeaderMode mode) {
        // The generated Caddy config strips the full union and copies a per-mode subset. A name copied but
        // not stripped would be client-spoofable, which is the failure mode §2.3 exists to prevent — so the
        // copy set for every mode must be contained in the strip set.
        var strip = IdentityForwarding.AllForwardableHeaderNames;
        foreach (var copied in IdentityForwarding.CopyHeaderNames(mode))
            Assert.Contains(copied, strip);
    }

    [Fact]
    public void TheJwtAssertion_IsCopiedInEveryMode() {
        // The signed assertion is the source of truth, not a mode-gated convenience: it is forwarded even
        // when no plaintext header is (mode None).
        foreach (var mode in Enum.GetValues<IdentityHeaderMode>())
            Assert.Contains(RouteAccessPolicy.JwtHeaderName, IdentityForwarding.CopyHeaderNames(mode));
    }

    [Fact]
    public void StripSet_IsTheUnionOfEveryModesCopySet() {
        // Defense in depth: a JWT-only route still strips the names some other mode would honour, so nothing
        // a client sends under any forwardable name can survive to the upstream.
        var union = Enum.GetValues<IdentityHeaderMode>()
            .SelectMany(IdentityForwarding.CopyHeaderNames)
            .Distinct();
        foreach (var name in union)
            Assert.Contains(name, IdentityForwarding.AllForwardableHeaderNames);
    }
}
