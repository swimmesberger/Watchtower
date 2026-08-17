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
    [InlineData(IdentityHeaderMode.Cloudflare)]
    public void EveryCopiedHeader_IsAlsoStripped(IdentityHeaderMode mode) {
        // The generated Caddy config strips the full ecosystem authz namespace and copies a per-mode subset.
        // A name copied but not stripped would be client-spoofable, which is the failure mode §2.3 exists to
        // prevent — so the copy set for every mode must be contained in the strip set (the load-bearing
        // invariant: CopyHeaderNames(mode) ⊆ StripHeaderNames).
        foreach (var copied in IdentityForwarding.CopyHeaderNames(mode))
            Assert.Contains(copied, IdentityForwarding.StripHeaderNames);
    }

    [Fact]
    public void TheJwtAssertion_IsCopiedInEveryMode() {
        // The signed assertion is the source of truth, not a mode-gated convenience: it is forwarded even
        // when no plaintext header is (mode None).
        foreach (var mode in Enum.GetValues<IdentityHeaderMode>())
            Assert.Contains(RouteAccessPolicy.JwtHeaderName, IdentityForwarding.CopyHeaderNames(mode));
    }

    [Fact]
    public void CloudflareMode_ForwardsTheCfVocabulary() {
        // The portability contract: an app written against Cloudflare Access headers keeps working
        // behind integrated auth — the email under Cloudflare's name, and the assertion duplicated
        // under Cf-Access-Jwt-Assertion (set by the verify endpoint alongside X-Watchtower-Jwt).
        var copied = IdentityForwarding.CopyHeaderNames(IdentityHeaderMode.Cloudflare);
        Assert.Contains(IdentityForwarding.CfAccessEmail, copied);
        Assert.Contains(IdentityForwarding.CfAccessJwtAssertion, copied);

        var headers = IdentityForwarding.PlaintextHeaders(
            IdentityHeaderMode.Cloudflare, "alice", "alice@example.com", ["admins"]).ToList();
        var header = Assert.Single(headers);
        Assert.Equal((IdentityForwarding.CfAccessEmail, "alice@example.com"), header);

        // No email ⇒ no plaintext header at all; the JWT (both names) still carries the identity.
        Assert.Empty(IdentityForwarding.PlaintextHeaders(IdentityHeaderMode.Cloudflare, "alice", null, []));
    }

    [Fact]
    public void StripSet_IsABroaderSupersetThanWhatIsForwarded() {
        // copy_headers governs only the verify response; every other client header reaches the upstream
        // untouched. So the strip set is deliberately broader than the union of what any mode forwards — it
        // neutralizes the full ecosystem authz vocabulary, including names we never populate.
        var forwarded = Enum.GetValues<IdentityHeaderMode>()
            .SelectMany(IdentityForwarding.CopyHeaderNames)
            .Distinct()
            .ToList();

        foreach (var name in forwarded)
            Assert.Contains(name, IdentityForwarding.StripHeaderNames);

        // The two group names we DO forward are in both sets — the invariant every forwarded name has to
        // satisfy, and the reason a verified group header cannot be a client's.
        Assert.Contains(IdentityForwarding.RemoteGroups, forwarded);
        Assert.Contains(IdentityForwarding.AuthRequestGroups, forwarded);

        // The names we never populate are still stripped — that is the whole point of the set being broader.
        Assert.DoesNotContain(IdentityForwarding.ForwardedGroups, forwarded);
        Assert.DoesNotContain(IdentityForwarding.ForwardedUser, forwarded);
        Assert.DoesNotContain(IdentityForwarding.AuthRequestAccessToken, forwarded);
        Assert.True(IdentityForwarding.StripHeaderNames.Length > forwarded.Count);
    }

    [Fact]
    public void GroupHeader_IsSortedAndCommaJoined() {
        // Ordinal sort, whatever order the membership rows came back in: what an upstream parses must not
        // depend on that. The comma is the separator both ecosystems use.
        Assert.Equal("admins,platform,viewers", GroupHeaderFor(["viewers", "admins", "platform"]));
    }

    [Fact]
    public void GroupHeader_IsAbsentForAnAccountInNoGroup() {
        // Absent, not empty: some upstreams read `Remote-Groups: ` as membership of a group named "".
        Assert.Null(GroupHeaderFor([]));
        // The same goes for a list that turns out to contain nothing emittable.
        Assert.Null(GroupHeaderFor(["  ", "bad,name"]));
    }

    /// <summary>
    /// The defense-in-depth filter, and specifically that it drops names <em>individually</em>. Neither of
    /// these can be created through the Groups module — <c>GroupMapping.ValidateName</c> refuses both — so
    /// this covers a row written by something else, where the damage of getting it wrong differs per case:
    /// a comma would fabricate a membership, while a non-ASCII name would fail the caller's header-safety
    /// filter on the joined value and silently cost the account every other group it is in.
    /// </summary>
    [Theory]
    // Would be read downstream as two memberships.
    [InlineData("viewers,admins")]
    // Would take the whole header down with it once HeaderSafe judges the joined value.
    [InlineData("Überwacher")]
    [InlineData("view\ners")]
    [InlineData("viewers\u007F")]
    public void GroupHeader_DropsOnlyTheUnemittableName_KeepingTheRest(string unemittable) {
        var header = GroupHeaderFor(["admins", unemittable, "viewers"]);

        Assert.Equal("admins,viewers", header);
        // And the joined value is something the endpoint's header-safety filter will actually accept.
        Assert.All(header!, c => Assert.InRange(c, ' ', '~'));
    }

    /// <summary>The group header a <c>Remote</c> route would forward for <paramref name="groups"/>, or null.</summary>
    private static string? GroupHeaderFor(string[] groups) =>
        IdentityForwarding
            .PlaintextHeaders(IdentityHeaderMode.Remote, "alice", email: null, groups)
            .Where(h => h.Name == IdentityForwarding.RemoteGroups)
            .Select(h => h.Value)
            .SingleOrDefault();

    [Fact]
    public void StripSet_NeutralizesGroupAndAccessTokenHeaders_ButNotTheTransportHeaders() {
        var strip = IdentityForwarding.StripHeaderNames;

        // The escalation vector: a group-aware upstream must never see a client-supplied group header.
        Assert.Contains(IdentityForwarding.RemoteGroups, strip);
        Assert.Contains(IdentityForwarding.AuthRequestGroups, strip);
        Assert.Contains(IdentityForwarding.ForwardedGroups, strip);
        Assert.Contains(IdentityForwarding.AuthRequestAccessToken, strip);
        Assert.Contains(IdentityForwarding.ForwardedUser, strip);

        // ...but the transport headers Caddy sets legitimately are left alone (enumerated by name, no prefix).
        Assert.DoesNotContain("X-Forwarded-For", strip);
        Assert.DoesNotContain("X-Forwarded-Proto", strip);
        Assert.DoesNotContain("X-Forwarded-Host", strip);
    }
}
