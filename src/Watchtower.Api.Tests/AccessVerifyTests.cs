using System.Net;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The verify endpoint, exercised as Caddy's <c>forward_auth</c> drives it (docs/central-auth/design.md
/// §5). Every request here is shaped the way the proxy shapes one: a <c>GET /api/access/verify</c>
/// carrying the original request's headers plus <c>X-Forwarded-Method</c>/<c>-Uri</c>/<c>-Host</c>.
/// </summary>
/// <remarks>
/// This is the one endpoint whose status code <em>is</em> the access decision, so the tests assert on
/// status and headers rather than on bodies — and cover the refusals at least as closely as the success,
/// because a wrong 200 here is an unauthenticated stranger inside someone's application.
/// </remarks>
public sealed class AccessVerifyTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AppDomain = "app.example.invalid";
    private const string HtmlAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    private static (string Key, string? Value)[] AuthOn(params (string Key, string? Value)[] extra) =>
        [("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost), .. extra];

    [Fact]
    public async Task UnknownHost_IsNotAWatchtowerApp() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();

        var response = await Send(client, Verify("nobody.example.invalid"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MissingForwardedHost_IsRefused() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        // Verify only ever runs behind the proxy, and the proxy always names the app. A bare request —
        // someone probing the endpoint directly — must not resolve to a route by accident.
        var response = await Send(client, new HttpRequestMessage(HttpMethod.Get, RouteAccessPolicy.VerifyPath));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublicRoute_PassesWithoutIdentity() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);

        // No forward_auth is emitted for a public route, so this only happens with a stale config; letting
        // the request through is what the proxy would have done anyway.
        var response = await Send(client, Verify(AppDomain));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoIdentityHeaders(response);
    }

    [Theory]
    [InlineData("/webhooks/github")]
    [InlineData("/healthz")]
    [InlineData("/webhooks/github?ref=main")]
    // Reserved: the callback has to answer while the visitor is still anonymous, or the dance cannot start.
    [InlineData("/.watchtower/callback?code=abc")]
    public async Task ExemptPaths_PassAnonymously_AndCarryNoIdentity(string uri) {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated, bypassPaths: "/webhooks/\n/healthz");

        var response = await Send(client, Verify(AppDomain, uri));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // A bypass is "no access control on this path", not "anonymous access as somebody" — forwarding a
        // user header here would let an unauthenticated caller be mistaken for a signed-in one.
        AssertNoIdentityHeaders(response);
    }

    [Fact]
    public async Task ExemptPaths_DoNotStretchToDotSegments() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated, bypassPaths: "/webhooks/");

        // Caddy matches its handlers on the cleaned path but forwards the original, so an exemption decided
        // on the raw string would be handing out /admin under the name of /webhooks/.
        var response = await Send(client, Verify(AppDomain, "/webhooks/../admin", accept: HtmlAccept));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousNavigation_IsSentToTheCentralLogin() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        var response = await Send(client, Verify(AppDomain, "/reports?range=30d", accept: HtmlAccept));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        // Built from configuration and the route row: literal https, the configured auth host, and the
        // route's own domain. X-Forwarded-Proto is not consulted, and the caller's host header never
        // reaches the target.
        Assert.Equal($"https://{AuthHost}/login", location.GetLeftPart(UriPartial.Path));
        Assert.Equal(
            $"?redirect_uri={Uri.EscapeDataString($"https://{AppDomain}/reports?range=30d")}",
            location.Query);
    }

    [Fact]
    public async Task AnonymousNavigation_IgnoresASpoofedSchemeAndHost() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        var request = Verify(AppDomain, "/reports", accept: HtmlAccept);
        request.Headers.Add("X-Forwarded-Proto", "http");

        var response = await Send(client, request);

        var location = response.Headers.Location!.ToString();
        Assert.StartsWith($"https://{AuthHost}/login", location, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", location.Replace("https://", "", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Theory]
    // An XHR wants a status it can branch on, not a login page it cannot render.
    [InlineData("GET", "application/json")]
    // And a POST must never be bounced into a login form: the body would be lost and the retry is not ours.
    [InlineData("POST", HtmlAccept)]
    [InlineData("PUT", HtmlAccept)]
    public async Task AnonymousNonNavigation_Gets401(string method, string accept) {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        var response = await Send(client, Verify(AppDomain, "/api/items", accept: accept, method: method));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task WithoutAnAuthHost_EvenNavigationsGet401() {
        using var factory = new WatchtowerApiFactory([("Watchtower:Auth:Enabled", "true")]);
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        // There is nowhere to send them: guessing a login host would be inventing a redirect target out of
        // request data, which is exactly what the open-redirect guard exists to prevent.
        var response = await Send(client, Verify(AppDomain, "/reports", accept: HtmlAccept));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SessionForAnotherApp_DoesNotOpenThisOne() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var otherRoute = await factory.AddRouteAsync("other.example.invalid", AccessMode.Authenticated);
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var userId = await factory.AddUserAsync("alice");
        var token = await factory.AppSessionAsync(userId, otherRoute);

        var response = await Send(client, Verify(AppDomain, "/", cookie: AccessCookie(token), accept: HtmlAccept));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task DisabledAccount_LosesAccessImmediately() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var userId = await factory.AddUserAsync("alice");
        var token = await factory.AppSessionAsync(userId, routeId);

        Assert.Equal(HttpStatusCode.OK,
            (await Send(client, Verify(AppDomain, "/", cookie: AccessCookie(token)))).StatusCode);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<Application.Persistence.WatchtowerDbContext>();
            var user = await db.Users.FindAsync([userId], TestContext.Current.CancellationToken);
            user!.Disabled = true;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        // Sessions are rows, not self-contained tickets, so disabling the account takes effect on the very
        // next proxied request rather than whenever a cookie would have expired.
        var afterDisable = await Send(client, Verify(AppDomain, "/", cookie: AccessCookie(token)));
        Assert.Equal(HttpStatusCode.Unauthorized, afterDisable.StatusCode);
    }

    [Fact]
    public async Task RestrictedWithoutAGrant_IsDenied_AndAudited() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Restricted);
        var userId = await factory.AddUserAsync("alice");
        var token = await factory.AppSessionAsync(userId, routeId);

        var response = await Send(client, Verify(AppDomain, "/", cookie: AccessCookie(token), accept: HtmlAccept));

        // Not a redirect: they have already signed in, so sending them back to the login page would loop.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        AssertNoIdentityHeaders(response);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(AppDomain, body, StringComparison.Ordinal);

        var denial = Assert.Single(await factory.AuditEventsAsync(), e => e.Action == "access.denied");
        Assert.False(denial.Success);
        Assert.Equal(AppDomain, denial.Target);
    }

    [Fact]
    public async Task JwtOnlyMode_ForwardsTheAssertion_ButNoPlaintextHeader() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        // The default: identity forwarding is JWT-only unless the route opts a plaintext mode in.
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");
        var token = await factory.AppSessionAsync(userId, routeId);

        var response = await Send(client, Verify(AppDomain, "/", cookie: AccessCookie(token)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The signed assertion is always present — it is the source of truth.
        Assert.True(response.Headers.Contains(RouteAccessPolicy.JwtHeaderName));
        // ...but not a single plaintext identity header, under any ecosystem's names.
        foreach (var header in new[] {
            IdentityForwarding.RemoteUser, IdentityForwarding.RemoteName, IdentityForwarding.RemoteEmail,
            IdentityForwarding.AuthRequestUser, IdentityForwarding.AuthRequestPreferredUsername,
            IdentityForwarding.AuthRequestEmail,
        })
            Assert.False(response.Headers.Contains(header), $"{header} must not be set in JWT-only mode.");
    }

    [Fact]
    public async Task RemoteMode_ForwardsTheAutheliaTraefikHeaders() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(
            AppDomain, AccessMode.Restricted, identityHeaderMode: IdentityHeaderMode.Remote);
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");
        await factory.GrantAsync(routeId, userId);
        var token = await factory.AppSessionAsync(userId, routeId);

        var response = await Send(client, Verify(AppDomain, "/", cookie: AccessCookie(token)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(RouteAccessPolicy.JwtHeaderName));
        Assert.Equal("alice", Header(response, IdentityForwarding.RemoteUser));
        Assert.Equal("alice", Header(response, IdentityForwarding.RemoteName));
        Assert.Equal("alice@example.invalid", Header(response, IdentityForwarding.RemoteEmail));
    }

    [Fact]
    public async Task AuthRequestMode_ForwardsTheOauth2ProxyHeaders() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(
            AppDomain, AccessMode.Authenticated, identityHeaderMode: IdentityHeaderMode.AuthRequest);
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");
        var token = await factory.AppSessionAsync(userId, routeId);

        var response = await Send(client, Verify(AppDomain, "/", cookie: AccessCookie(token)));

        Assert.Equal("alice", Header(response, IdentityForwarding.AuthRequestUser));
        Assert.Equal("alice", Header(response, IdentityForwarding.AuthRequestPreferredUsername));
        Assert.Equal("alice@example.invalid", Header(response, IdentityForwarding.AuthRequestEmail));
    }

    [Fact]
    public async Task AccountWithoutAnEmail_OmitsTheHeaderRatherThanSendingItEmpty() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(
            AppDomain, AccessMode.Authenticated, identityHeaderMode: IdentityHeaderMode.Remote);
        var userId = await factory.AddUserAsync("alice");
        var token = await factory.AppSessionAsync(userId, routeId);

        var response = await Send(client, Verify(AppDomain, "/", cookie: AccessCookie(token)));

        Assert.Equal("alice", Header(response, IdentityForwarding.RemoteUser));
        Assert.False(response.Headers.Contains(IdentityForwarding.RemoteEmail));
    }

    [Fact]
    public async Task SmuggledIdentityHeaders_AreReplacedByTheVerifiedOnes() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(
            AppDomain, AccessMode.Authenticated, identityHeaderMode: IdentityHeaderMode.Remote);
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");
        var token = await factory.AppSessionAsync(userId, routeId);

        var request = Verify(AppDomain, "/", cookie: AccessCookie(token));
        request.Headers.Add(IdentityForwarding.RemoteUser, "root");
        request.Headers.Add(IdentityForwarding.RemoteEmail, "root@example.invalid");
        request.Headers.Add(RouteAccessPolicy.JwtHeaderName, "forged");

        var response = await Send(client, request);

        // The generated Caddy config strips these before we ever see them; this asserts the second half of
        // the same property — what we answer with is derived from the session, never echoed from the request.
        Assert.Equal("alice", Header(response, IdentityForwarding.RemoteUser));
        Assert.Equal("alice@example.invalid", Header(response, IdentityForwarding.RemoteEmail));
        Assert.NotEqual("forged", Header(response, RouteAccessPolicy.JwtHeaderName));
    }

    [Fact]
    public async Task ForwardedAssertion_VerifiesAgainstThePublishedJwks() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");
        var token = await factory.AppSessionAsync(userId, routeId);
        var ct = TestContext.Current.CancellationToken;

        var response = await Send(client, Verify(AppDomain, "/", cookie: AccessCookie(token)));
        var assertion = Header(response, RouteAccessPolicy.JwtHeaderName);

        // Exactly what a protected application does: fetch the JWKS (anonymously) and validate.
        var jwks = await client.GetAsync("/api/auth/jwks", ct);
        Assert.Equal(HttpStatusCode.OK, jwks.StatusCode);
        Assert.Equal("application/jwk-set+json", jwks.Content.Headers.ContentType?.MediaType);

        var keys = JsonWebKeySet.Create(await jwks.Content.ReadAsStringAsync(ct));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(assertion, new TokenValidationParameters {
            ValidIssuer = AuthHost,
            ValidAudience = AppDomain,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });

        Assert.True(result.IsValid, result.Exception?.ToString());
        var jwt = Assert.IsType<JsonWebToken>(result.SecurityToken);
        Assert.Equal(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), jwt.Subject);
        Assert.Equal("alice", jwt.GetClaim("preferred_username").Value);
    }

    // ── Group grants and group forwarding ─────────────────────────────────────

    /// <summary>
    /// The end-to-end shape of a group grant: a member of a granted group passes verify and is handed the
    /// identity headers, while a signed-in account that is neither a member nor directly granted is refused
    /// with the same 403 a missing direct grant produces.
    /// </summary>
    [Fact]
    public async Task AMemberOfAGrantedGroup_PassesVerify_AndANonMemberIsRefused() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(
            AppDomain, AccessMode.Restricted, identityHeaderMode: IdentityHeaderMode.Remote);
        var alice = await factory.AddUserAsync("alice", "alice@example.invalid");
        var bob = await factory.AddUserAsync("bob");
        var groupId = await factory.AddGroupAsync("platform", alice);
        await factory.GrantGroupAsync(routeId, groupId);

        var allowed = await Send(client, Verify(
            AppDomain, "/", cookie: AccessCookie(await factory.AppSessionAsync(alice, routeId))));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True(allowed.Headers.Contains(RouteAccessPolicy.JwtHeaderName));
        Assert.Equal("alice", Header(allowed, IdentityForwarding.RemoteUser));
        Assert.Equal("platform", Header(allowed, IdentityForwarding.RemoteGroups));

        var refused = await Send(client, Verify(
            AppDomain, "/", cookie: AccessCookie(await factory.AppSessionAsync(bob, routeId))));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        AssertNoIdentityHeaders(refused);
    }

    /// <summary>
    /// Membership is read per request, so revoking it takes effect on the next one — there is no cached
    /// decision to invalidate, and the app session minted while she was a member does not survive it.
    /// </summary>
    [Fact]
    public async Task RevokingMembership_ClosesTheRouteOnTheVeryNextRequest() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Restricted);
        var alice = await factory.AddUserAsync("alice");
        var groupId = await factory.AddGroupAsync("platform", alice);
        await factory.GrantGroupAsync(routeId, groupId);
        var cookie = AccessCookie(await factory.AppSessionAsync(alice, routeId));

        Assert.Equal(HttpStatusCode.OK, (await Send(client, Verify(AppDomain, "/", cookie: cookie))).StatusCode);

        await factory.RemoveFromGroupAsync(groupId, alice);

        Assert.Equal(
            HttpStatusCode.Forbidden, (await Send(client, Verify(AppDomain, "/", cookie: cookie))).StatusCode);
    }

    /// <summary>
    /// The header form of a multi-group membership: one comma-joined value in ordinal order, so what an
    /// upstream parses does not depend on the order the membership rows came back in.
    /// </summary>
    [Theory]
    [InlineData(IdentityHeaderMode.Remote)]
    [InlineData(IdentityHeaderMode.AuthRequest)]
    public async Task GroupsAreForwarded_CommaJoinedAndSorted_UnderTheModesOwnName(IdentityHeaderMode mode) {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(
            AppDomain, AccessMode.Authenticated, identityHeaderMode: mode);
        var alice = await factory.AddUserAsync("alice");
        // Created out of order on purpose — the emitted order must be the sort, not the insertion.
        await factory.AddGroupAsync("viewers", alice);
        await factory.AddGroupAsync("admins", alice);

        var response = await Send(client, Verify(
            AppDomain, "/", cookie: AccessCookie(await factory.AppSessionAsync(alice, routeId))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (own, other) = mode == IdentityHeaderMode.Remote
            ? (IdentityForwarding.RemoteGroups, IdentityForwarding.AuthRequestGroups)
            : (IdentityForwarding.AuthRequestGroups, IdentityForwarding.RemoteGroups);

        Assert.Equal("admins,viewers", Header(response, own));
        // Only the mode's own vocabulary is populated — the other ecosystem's name stays absent.
        Assert.False(response.Headers.Contains(other));
        Assert.False(response.Headers.Contains(IdentityForwarding.ForwardedGroups));
    }

    [Fact]
    public async Task AnAccountInNoGroup_OmitsTheGroupHeaderRatherThanSendingItEmpty() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(
            AppDomain, AccessMode.Authenticated, identityHeaderMode: IdentityHeaderMode.Remote);
        var alice = await factory.AddUserAsync("alice");

        var response = await Send(client, Verify(
            AppDomain, "/", cookie: AccessCookie(await factory.AppSessionAsync(alice, routeId))));

        Assert.Equal("alice", Header(response, IdentityForwarding.RemoteUser));
        // An empty header is not the same statement as an absent one: some upstreams read `Remote-Groups: `
        // as membership of a group whose name is the empty string.
        Assert.False(response.Headers.Contains(IdentityForwarding.RemoteGroups));
    }

    /// <summary>
    /// A <c>None</c> route forwards no plaintext group header — but the assertion still carries the groups,
    /// because the JWT is the trusted channel, not a mode-gated convenience.
    /// </summary>
    [Fact]
    public async Task JwtOnlyMode_CarriesGroupsInTheAssertion_AndNoGroupHeader() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var alice = await factory.AddUserAsync("alice");
        await factory.AddGroupAsync("viewers", alice);
        await factory.AddGroupAsync("admins", alice);

        var response = await Send(client, Verify(
            AppDomain, "/", cookie: AccessCookie(await factory.AppSessionAsync(alice, routeId))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(IdentityForwarding.RemoteGroups));
        Assert.False(response.Headers.Contains(IdentityForwarding.AuthRequestGroups));

        var jwt = new JsonWebToken(Header(response, RouteAccessPolicy.JwtHeaderName));
        Assert.Equal(["admins", "viewers"], GroupsOf(jwt));
    }

    [Fact]
    public async Task TheAssertionsGroupsClaim_IsPresentAndEmptyForAnAccountInNoGroup() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var alice = await factory.AddUserAsync("alice");

        var response = await Send(client, Verify(
            AppDomain, "/", cookie: AccessCookie(await factory.AppSessionAsync(alice, routeId))));

        // Stated rather than omitted: an app mapping groups onto roles has to be able to tell "in no group"
        // apart from "this deployment does not answer group questions".
        var jwt = new JsonWebToken(Header(response, RouteAccessPolicy.JwtHeaderName));
        Assert.True(jwt.TryGetPayloadValue<string[]>("groups", out var groups));
        Assert.Empty(groups);
    }

    /// <summary>
    /// The second half of the anti-spoofing property (the first is the strip list in the generated Caddy
    /// config): what we answer with is derived from the account's memberships, never echoed from the
    /// request. A low-privilege user asserting <c>Remote-Groups: admins</c> gets their real groups back.
    /// </summary>
    [Fact]
    public async Task ASmuggledGroupHeader_IsReplacedByTheVerifiedMembership() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(
            AppDomain, AccessMode.Authenticated, identityHeaderMode: IdentityHeaderMode.Remote);
        var alice = await factory.AddUserAsync("alice");
        await factory.AddGroupAsync("viewers", alice);

        var request = Verify(AppDomain, "/", cookie: AccessCookie(await factory.AppSessionAsync(alice, routeId)));
        request.Headers.Add(IdentityForwarding.RemoteGroups, "admins");

        var response = await Send(client, request);

        Assert.Equal("viewers", Header(response, IdentityForwarding.RemoteGroups));
    }

    [Fact]
    public async Task WithAuthDisabled_TheForwardAuthSurfaceIsNotThere() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, Verify(AppDomain))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/jwks", ct)).StatusCode);
        // Explicitly 404 rather than left unmapped: the SPA fallback would otherwise answer a callback with
        // index.html and a 200.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync(RouteAccessPolicy.CallbackPath + "?code=x", ct)).StatusCode);
    }

    // ── Request shaping ───────────────────────────────────────────────────────

    /// <summary>Builds the request Caddy's <c>forward_auth</c> makes for one original request.</summary>
    private static HttpRequestMessage Verify(
        string host, string uri = "/", string? cookie = null, string? accept = null, string method = "GET") {
        // forward_auth always calls the target with GET and puts the original method in a header.
        var request = new HttpRequestMessage(HttpMethod.Get, RouteAccessPolicy.VerifyPath);
        request.Headers.Add("X-Forwarded-Host", host);
        request.Headers.Add("X-Forwarded-Uri", uri);
        request.Headers.Add("X-Forwarded-Method", method);
        if (accept is not null) request.Headers.Add("Accept", accept);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static string AccessCookie(string token) => $"{AuthSessionService.AccessCookieName}={token}";

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpRequestMessage request) =>
        client.SendAsync(request, TestContext.Current.CancellationToken);

    private static string Header(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));

    /// <summary>
    /// The <c>groups</c> claim of a minted assertion, read off the payload rather than through
    /// <c>Claims</c> — the claim is a JSON array, and the flattened claim collection would report it as
    /// several single-valued entries (or none at all when it is empty), which is not the shape an upstream
    /// parsing the payload sees.
    /// </summary>
    private static IReadOnlyList<string> GroupsOf(JsonWebToken jwt) =>
        jwt.GetPayloadValue<string[]>("groups");

    private static void AssertNoIdentityHeaders(HttpResponseMessage response) {
        Assert.False(response.Headers.Contains(RouteAccessPolicy.JwtHeaderName),
            $"{RouteAccessPolicy.JwtHeaderName} must not be set here.");
        foreach (var header in IdentityForwarding.StripHeaderNames)
            Assert.False(response.Headers.Contains(header), $"{header} must not be set here.");
    }
}
