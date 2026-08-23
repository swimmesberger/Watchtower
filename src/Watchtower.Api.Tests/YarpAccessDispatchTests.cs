using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Access control on the in-process proxy's request path (ADR-0022): the same verdicts
/// <c>AccessVerifyTests</c> pins down for Caddy's <c>forward_auth</c> hop, reached without one.
/// </summary>
/// <remarks>
/// The point of the design is that there is only one decision — <see cref="AccessVerifier"/> — and two
/// transports acting on it, so these tests are deliberately shaped like their verify counterparts: the same
/// estate, the same refusals, the same login <c>Location</c>. What is new here is what happens
/// <em>after</em> a verdict, which the endpoint never had to answer: the identity headers land on the
/// outgoing request rather than on a response Caddy copies, and the requests that are refused must not
/// reach the upstream at all.
/// </remarks>
public sealed class YarpAccessDispatchTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AppDomain = "app.example.invalid";
    private const string AppUrl = $"https://{AppDomain}/reports?range=30d";
    private const string HtmlAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    private static WatchtowerApiFactory AuthOn(params (string Key, string? Value)[] extra) =>
        WatchtowerApiFactory.WithYarpProxy([
            ("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost), .. extra]);

    [Fact]
    public async Task AnonymousNavigation_IsRedirectedToTheRealmsLoginHost() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        await factory.ApplyProxyAsync();

        var response = await client.SendAsync(Navigate($"{AppUrl}"), Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        // Character for character what the verify endpoint answers Caddy with — the URL is built by the
        // shared verifier, from configuration and the route row, and neither transport touches it.
        Assert.Equal(
            $"https://{AuthHost}/login?redirect_uri={Uri.EscapeDataString(AppUrl)}",
            response.Headers.Location?.ToString());
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    [Fact]
    public async Task AnonymousXhr_Gets401_AndIsNotForwarded() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        await factory.ApplyProxyAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{AppDomain}/api/items");
        request.Headers.Add("Accept", "application/json");
        var response = await client.SendAsync(request, Ct);

        // A caller that cannot render a login page gets a status it can branch on instead.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// <c>X-Forwarded-Method</c> is how Caddy tells the verify endpoint what the original request was, and
    /// in process there is no Caddy — so it is a header the <em>client</em> wrote. Honouring it would let a
    /// POST dress itself as a navigation and collect a login redirect instead of the 401 it is owed, which
    /// on a browser-driven flow is a body silently replayed nowhere useful.
    /// </summary>
    [Fact]
    public async Task ASpoofedForwardedMethod_DoesNotTurnAPostIntoANavigation() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        await factory.ApplyProxyAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{AppDomain}/items") {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Accept", HtmlAccept);
        request.Headers.Add("X-Forwarded-Method", "GET");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// The same two headers on a request that is allowed through: they describe a hop that did not happen,
    /// so they are removed rather than relayed to an upstream that might read them as the proxy's word.
    /// </summary>
    [Fact]
    public async Task TheCaddyHopHeaders_DoNotReachTheUpstream() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{AppDomain}/");
        request.Headers.Add("X-Forwarded-Method", "DELETE");
        request.Headers.Add("X-Forwarded-Uri", "/admin");
        await client.SendAsync(request, Ct);

        var forwarded = factory.Forwarder.Single();
        Assert.False(forwarded.Has("X-Forwarded-Method"));
        Assert.False(forwarded.Has("X-Forwarded-Uri"));
    }

    /// <summary>
    /// The success path, end to end: the assertion the upstream receives is a real one, and it verifies
    /// against the JWKS the same way an application behind the proxy would verify it.
    /// </summary>
    [Fact]
    public async Task AuthorizedRequest_IsForwardedWithTheIdentityHeaders() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(
            AppDomain, AccessMode.Restricted, identityHeaderMode: IdentityHeaderMode.Remote);
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");
        await factory.GrantAsync(routeId, userId);
        await factory.AddGroupAsync("platform", userId);
        var token = await factory.AppSessionAsync(userId, routeId);
        await factory.ApplyProxyAsync();

        var response = await client.SendAsync(WithSession($"https://{AppDomain}/", token), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var forwarded = factory.Forwarder.Single();
        // On the OUTGOING request, which is the difference from the verify endpoint: there the headers ride
        // the response for Caddy's copy_headers to move, here they are simply set on the way out.
        var assertion = forwarded.Header(RouteAccessPolicy.JwtHeaderName);
        Assert.NotNull(assertion);
        Assert.Equal("alice", forwarded.Header(IdentityForwarding.RemoteUser));
        Assert.Equal("alice@example.invalid", forwarded.Header(IdentityForwarding.RemoteEmail));
        Assert.Equal("platform", forwarded.Header(IdentityForwarding.RemoteGroups));

        // Exactly what a protected application does: fetch the JWKS and validate. Requested on a host
        // nobody routed, so it reaches Watchtower rather than being forwarded.
        var jwks = await client.GetAsync("/api/auth/jwks", Ct);
        var keys = JsonWebKeySet.Create(await jwks.Content.ReadAsStringAsync(Ct));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(assertion, new TokenValidationParameters {
            ValidIssuer = AuthHost,
            ValidAudience = AppDomain,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        });
        Assert.True(result.IsValid, result.Exception?.ToString());
        Assert.Equal(userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((JsonWebToken)result.SecurityToken).Subject);
    }

    /// <summary>
    /// The strip set is a deliberate superset of what any route forwards, and it is applied on every
    /// protected route regardless of mode: a JWT-only route still neutralises <c>Remote-Groups</c>, because
    /// a group-aware upstream would honour it as authoritative (design.md §2.3).
    /// </summary>
    [Fact]
    public async Task SmuggledIdentityHeaders_AreStripped_EvenOnAJwtOnlyRoute() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var userId = await factory.AddUserAsync("alice");
        var token = await factory.AppSessionAsync(userId, routeId);
        await factory.ApplyProxyAsync();

        // Every name in the deny-list at once, rather than a hand-picked few: the list is what the strip is
        // written against, so enumerating it here is what makes a name added to one and forgotten in the
        // other a failing test rather than a hole.
        const string Smuggled = "smuggled";
        var request = WithSession($"https://{AppDomain}/", token);
        foreach (var name in IdentityForwarding.StripHeaderNames) request.Headers.Add(name, Smuggled);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, Ct)).StatusCode);

        var forwarded = factory.Forwarder.Single();
        foreach (var name in IdentityForwarding.StripHeaderNames) {
            var value = forwarded.Header(name);
            // Either the name is gone — a JWT-only route forwards none of the plaintext vocabulary — or it
            // is the one name this route does populate, carrying the minted assertion. What it must never
            // be is the string the client wrote.
            Assert.NotEqual(Smuggled, value);
            if (value is not null)
                Assert.Equal(RouteAccessPolicy.JwtHeaderName, name);
        }
        // Named explicitly as well, so the loop above cannot pass by forwarding nothing at all.
        Assert.NotNull(forwarded.Header(RouteAccessPolicy.JwtHeaderName));
    }

    /// <summary>
    /// A bypass path is "no access control here", not "anonymous access as somebody" — so it passes without
    /// a session and carries no identity at all.
    /// </summary>
    [Fact]
    public async Task BypassPath_IsForwardedWithNoIdentity() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(
            AppDomain, AccessMode.Authenticated, bypassPaths: "/webhooks/",
            identityHeaderMode: IdentityHeaderMode.Remote);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AppDomain}/webhooks/github", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = factory.Forwarder.Single();
        Assert.False(forwarded.Has(RouteAccessPolicy.JwtHeaderName));
        foreach (var name in IdentityForwarding.StripHeaderNames)
            Assert.False(forwarded.Has(name), $"{name} must not reach the upstream on a bypass path.");
    }

    [Fact]
    public async Task RestrictedWithoutAGrant_Gets403Html_AndIsAudited() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Restricted);
        var userId = await factory.AddUserAsync("alice");
        var token = await factory.AppSessionAsync(userId, routeId);
        await factory.ApplyProxyAsync();

        var request = WithSession($"https://{AppDomain}/", token);
        request.Headers.Add("Accept", HtmlAccept);
        var response = await client.SendAsync(request, Ct);

        // Not a redirect: they have already signed in, so sending them back to the login page would loop.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains(AppDomain, body, StringComparison.Ordinal);
        Assert.Contains("Access denied", body, StringComparison.Ordinal);
        Assert.Empty(factory.Forwarder.Forwarded);

        var denial = Assert.Single(await factory.AuditEventsAsync(), e => e.Action == "access.denied");
        Assert.False(denial.Success);
        Assert.Equal(AppDomain, denial.Target);
    }

    /// <summary>
    /// The highest-risk regression in the whole phase, and the reason the dispatcher stamps
    /// <c>X-Forwarded-Host</c> on its <c>/.watchtower/*</c> fall-through: the callback binds the login code
    /// to the domain it is being redeemed on by reading that header, because with Caddy that is where the
    /// domain comes from. Without the stamp every cross-domain sign-in would answer "this link has expired"
    /// and no application behind the in-process proxy could be entered at all.
    /// </summary>
    [Fact]
    public async Task DotWatchtowerCallback_OnARouteHost_RedeemsTheCode() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        await factory.ApplyProxyAsync();

        // 1. Sign in centrally, on the login host, naming the app the visitor was heading for.
        var login = await client.PostAsJsonAsync(
            $"https://{AuthHost}/api/auth/login",
            new { userName = "admin", password = WatchtowerApiFactory.AdminPassword, redirectUri = AppUrl },
            Ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync(Ct));
        var continueUrl = loginBody.RootElement.GetProperty("continueUrl").GetString()!;
        Assert.StartsWith($"https://{AppDomain}{RouteAccessPolicy.CallbackPath}?code=", continueUrl,
            StringComparison.Ordinal);

        // 2. The browser follows it to the app's own domain. No X-Forwarded-* is sent — there is no proxy
        //    in front of us to send one — so the only way the callback learns the domain is the stamp.
        var callback = await client.GetAsync(continueUrl, Ct);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal(AppUrl, callback.Headers.Location?.ToString());
        var setCookie = Assert.Single(callback.Headers.GetValues("Set-Cookie"));
        Assert.Contains($"{AuthSessionService.AccessCookieName}=", setCookie, StringComparison.Ordinal);
        // The callback is Watchtower's, on the app's domain: it must never have left for the container.
        Assert.Empty(factory.Forwarder.Forwarded);

        // 3. And the cookie it minted is what opens the app on the very next request.
        var accessToken = setCookie.Split(';')[0].Split('=', 2)[1];
        var entered = await client.SendAsync(WithSession($"https://{AppDomain}/", accessToken), Ct);
        Assert.Equal(HttpStatusCode.OK, entered.StatusCode);
        Assert.Equal(RecordingHttpForwarder.MarkerBody, await entered.Content.ReadAsStringAsync(Ct));

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var session = await db.AuthSessions.AsNoTracking().SingleAsync(s => s.Kind == SessionKind.App, Ct);
            Assert.Equal(routeId, session.RouteId);
        });
    }

    /// <summary>
    /// The reserved prefix is Watchtower's on every protected app's domain — the same trade Cloudflare
    /// makes with <c>/cdn-cgi/</c>. An upstream that wanted those paths cannot have them, and more to the
    /// point, must not be able to answer them.
    /// </summary>
    [Theory]
    [InlineData(RouteAccessPolicy.AppLogoutPath)]
    [InlineData("/.watchtower/userinfo")]
    public async Task DotWatchtowerPaths_AreNeverForwarded(string path) {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var userId = await factory.AddUserAsync("alice");
        var token = await factory.AppSessionAsync(userId, routeId);
        await factory.ApplyProxyAsync();

        var response = await client.SendAsync(WithSession($"https://{AppDomain}{path}", token), Ct);

        // Whatever each endpoint answers, it is Watchtower answering.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    [Fact]
    public async Task PublicRoute_IsForwardedWithoutVerification() {
        using var factory = AuthOn();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.SendAsync(Navigate($"https://{AppDomain}/"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = factory.Forwarder.Single();
        Assert.False(forwarded.Has(RouteAccessPolicy.JwtHeaderName));
    }

    // ── Request shaping ───────────────────────────────────────────────────────

    /// <summary>A document fetch: what decides a login redirect over a bare 401.</summary>
    private static HttpRequestMessage Navigate(string url) {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", HtmlAccept);
        return request;
    }

    private static HttpRequestMessage WithSession(string url, string accessToken) {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{AuthSessionService.AccessCookieName}={accessToken}");
        return request;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
