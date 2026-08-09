using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The cross-domain hand-over (docs/central-auth/design.md §5): login (or an existing central session)
/// mints a one-time code, the app's own <c>/.watchtower/callback</c> exchanges it for that domain's
/// session, and <c>/.watchtower/logout</c> gives it back.
/// </summary>
/// <remarks>
/// Every step runs through the real pipeline, because the properties worth testing are pipeline
/// properties: which cookie is set with which attributes, what a reused code does, and whether a refusal
/// tells the caller anything it should not.
/// </remarks>
public sealed class LoginDanceTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AppDomain = "app.example.invalid";
    private const string AppUrl = $"https://{AppDomain}/reports?range=30d";

    private static (string Key, string? Value)[] AuthOn(params (string Key, string? Value)[] extra) =>
        [("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost), .. extra];

    [Fact]
    public async Task FullDance_SignsInCentrally_ThenMintsTheAppSession() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        // 1. The credentials go to the auth host, with the app URL the verify redirect put in the address bar.
        var login = await client.SendAsync(Login("admin", WatchtowerApiFactory.AdminPassword, AppUrl), ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        // The central session is established either way — the hand-over is a second, separate step.
        Assert.Contains(AuthSessionService.SsoCookieName, SetCookie(login), StringComparison.Ordinal);

        var continueUrl = await ContinueUrlOf(login);
        // Built from the route row, not from the string that was posted.
        Assert.StartsWith($"https://{AppDomain}{RouteAccessPolicy.CallbackPath}?code=", continueUrl,
            StringComparison.Ordinal);

        // 2. The browser follows it to the app's own domain, where Caddy proxies /.watchtower/* to us.
        var callback = await client.SendAsync(Callback(continueUrl), ct);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(AppUrl, callback.Headers.Location!.ToString());

        var accessCookie = SetCookie(callback);
        Assert.Contains($"{AuthSessionService.AccessCookieName}=", accessCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", accessCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", accessCookie, StringComparison.OrdinalIgnoreCase);
        // Host-scoped: a Domain attribute would hand this app's session to every sibling host.
        Assert.DoesNotContain("domain=", accessCookie, StringComparison.OrdinalIgnoreCase);

        // 3. And that cookie is what verify now accepts for this app.
        var token = accessCookie.Split(';')[0].Split('=', 2)[1];
        var verify = await client.SendAsync(Verify(AppDomain, token), ct);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var session = await db.AuthSessions.AsNoTracking().SingleAsync(s => s.Kind == SessionKind.App, ct);
            Assert.Equal(routeId, session.RouteId);
            // The code is gone the moment it is used.
            Assert.False(await db.LoginCodes.AnyAsync(ct));
        });
    }

    [Fact]
    public async Task ReusedCode_IsRefused() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        var login = await client.SendAsync(Login("admin", WatchtowerApiFactory.AdminPassword, AppUrl), ct);
        var continueUrl = await ContinueUrlOf(login);

        Assert.Equal(HttpStatusCode.Redirect, (await client.SendAsync(Callback(continueUrl), ct)).StatusCode);

        // The code travelled in a URL — through history, and any log on the way. Replaying it must buy
        // nothing, and must not leak whether it ever existed.
        var replay = await client.SendAsync(Callback(continueUrl), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.False(replay.Headers.Contains("Set-Cookie"));

        var unknown = await client.SendAsync(
            Callback($"https://{AppDomain}{RouteAccessPolicy.CallbackPath}?code=never-issued"), ct);
        Assert.Equal(replay.StatusCode, unknown.StatusCode);
        Assert.Equal(
            await replay.Content.ReadAsStringAsync(ct),
            await unknown.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task CodeRedeemedOnTheWrongDomain_IsRefused() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        await factory.AddRouteAsync("other.example.invalid", AccessMode.Authenticated);

        var login = await client.SendAsync(Login("admin", WatchtowerApiFactory.AdminPassword, AppUrl), ct);
        var continueUrl = await ContinueUrlOf(login);

        // Same Watchtower, different app: the code names its route, so presenting it elsewhere cannot
        // produce a session for the app it was not minted for.
        var request = Callback(continueUrl);
        request.Headers.Remove("X-Forwarded-Host");
        request.Headers.Add("X-Forwarded-Host", "other.example.invalid");

        var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task GrantRevokedBetweenMintAndRedeem_StopsTheHandover() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Restricted);
        var userId = await AdminIdAsync(factory);
        await factory.GrantAsync(routeId, userId);

        var login = await client.SendAsync(Login("admin", WatchtowerApiFactory.AdminPassword, AppUrl), ct);
        var continueUrl = await ContinueUrlOf(login);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            await db.RouteAccessGrants.Where(g => g.RouteId == routeId).ExecuteDeleteAsync(ct);
        });

        // Authorisation is re-checked at redemption rather than trusted from mint time — a revoked grant
        // must not stay usable for the sixty seconds the code lives.
        var response = await client.SendAsync(Callback(continueUrl), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    // Not a route Watchtower serves — the open-redirect guard (design.md §9).
    [InlineData("https://evil.example.net/")]
    // A public app has no session to hand over, so a code for one would only be a redirect primitive.
    [InlineData("https://public.example.invalid/")]
    // Shapes that could never be an app URL.
    [InlineData("http://app.example.invalid/")]
    [InlineData("/reports")]
    public async Task RefusedRedirectUris_AllLookTheSame(string redirectUri) {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        await factory.AddRouteAsync("public.example.invalid", AccessMode.Public);

        var response = await client.SendAsync(Login("admin", WatchtowerApiFactory.AdminPassword, redirectUri), ct);

        // One answer for "no such app", "that app is public" and "not a URL": distinguishing them would let
        // any account enumerate the route table and its policy.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The credentials were good, so the central session still exists — the visitor is signed in to
        // Watchtower, just not admitted to that app.
        Assert.Contains(AuthSessionService.SsoCookieName, SetCookie(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestrictedWithoutAGrant_IsRefusedAtLogin_WithTheSameBody() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(AppDomain, AccessMode.Restricted);

        var denied = await client.SendAsync(Login("admin", WatchtowerApiFactory.AdminPassword, AppUrl), ct);
        var unknown = await client.SendAsync(
            Login("admin", WatchtowerApiFactory.AdminPassword, "https://evil.example.net/"), ct);

        // Failing fast here rather than redirecting is what keeps the visitor out of a login/deny loop.
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(ct),
            await denied.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task SilentSso_HandsAnExistingSessionToTheNextApp() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var sso = await factory.SsoSessionAsync(await AdminIdAsync(factory));

        var response = await client.SendAsync(Continue(AppUrl, sso), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var continueUrl = await ContinueUrlOf(response);
        Assert.StartsWith($"https://{AppDomain}{RouteAccessPolicy.CallbackPath}?code=", continueUrl,
            StringComparison.Ordinal);

        // And it really is a working hand-over, not just a well-formed URL.
        Assert.Equal(HttpStatusCode.Redirect, (await client.SendAsync(Callback(continueUrl), ct)).StatusCode);
    }

    [Fact]
    public async Task SilentSso_NeedsACentralSession() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Continue(AppUrl, ssoToken: null), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Continue(AppUrl, "not-a-session"), ct)).StatusCode);
    }

    [Fact]
    public async Task SilentSso_RefusesAnAppTheAccountMayNotEnter() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(AppDomain, AccessMode.Restricted);
        var sso = await factory.SsoSessionAsync(await AdminIdAsync(factory));

        var response = await client.SendAsync(Continue(AppUrl, sso), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            // Nothing redeemable is left lying around after a refusal.
            Assert.False(await db.LoginCodes.AnyAsync(ct));
        });
    }

    [Fact]
    public async Task SilentSso_RequiresJson() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var sso = await factory.SsoSessionAsync(await AdminIdAsync(factory));

        // A cross-site HTML form can only produce these content types; refusing them is what stops a forged
        // POST from minting a code with somebody else's session.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/continue") {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("redirectUri", AppUrl)]),
        };
        request.Headers.Add("Cookie", $"{AuthSessionService.SsoCookieName}={sso}");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.SendAsync(request, ct)).StatusCode);
    }

    [Fact]
    public async Task PerAppLogout_ClearsOnlyThatDomain() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var userId = await AdminIdAsync(factory);
        var sso = await factory.SsoSessionAsync(userId);
        var appToken = await factory.AppSessionAsync(userId, routeId);

        var request = new HttpRequestMessage(HttpMethod.Get, RouteAccessPolicy.AppLogoutPath);
        request.Headers.Add("X-Forwarded-Host", AppDomain);
        request.Headers.Add("Cookie", $"{AuthSessionService.AccessCookieName}={appToken}");
        var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
        // The clearing cookie has to repeat the attributes it was set with, or the browser keeps the old one.
        var cleared = SetCookie(response);
        Assert.Contains($"{AuthSessionService.AccessCookieName}=;", cleared, StringComparison.Ordinal);
        Assert.Contains("path=/", cleared, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Verify(AppDomain, appToken), ct)).StatusCode);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            // Signing out of one app is not signing out of Watchtower.
            Assert.True(await db.AuthSessions.AnyAsync(s => s.Kind == SessionKind.Sso, ct));
            Assert.False(await db.AuthSessions.AnyAsync(s => s.Kind == SessionKind.App, ct));
        });
        Assert.NotNull(sso);
    }

    [Fact]
    public async Task PlainLogin_IsUnchangedWhenNoAppIsInvolved() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.SendAsync(Login("admin", WatchtowerApiFactory.AdminPassword, redirectUri: null), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("admin", body.RootElement.GetProperty("userName").GetString());
        // Omitted rather than null: the SPA branches on its presence.
        Assert.False(body.RootElement.TryGetProperty("continueUrl", out _));
    }

    // ── Request shaping ───────────────────────────────────────────────────────

    private static HttpRequestMessage Login(string userName, string password, string? redirectUri) =>
        new(HttpMethod.Post, "/api/auth/login") {
            Content = JsonContent.Create(new { userName, password, redirectUri }),
        };

    private static HttpRequestMessage Continue(string redirectUri, string? ssoToken) {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/continue") {
            Content = JsonContent.Create(new { redirectUri }),
        };
        if (ssoToken is not null)
            request.Headers.Add("Cookie", $"{AuthSessionService.SsoCookieName}={ssoToken}");
        return request;
    }

    /// <summary>The callback as Caddy proxies it: the app's own domain named in <c>X-Forwarded-Host</c>.</summary>
    private static HttpRequestMessage Callback(string continueUrl) {
        var uri = new Uri(continueUrl);
        var request = new HttpRequestMessage(HttpMethod.Get, uri.PathAndQuery);
        request.Headers.Add("X-Forwarded-Host", uri.Host);
        return request;
    }

    private static HttpRequestMessage Verify(string host, string accessToken) {
        var request = new HttpRequestMessage(HttpMethod.Get, RouteAccessPolicy.VerifyPath);
        request.Headers.Add("X-Forwarded-Host", host);
        request.Headers.Add("X-Forwarded-Uri", "/");
        request.Headers.Add("X-Forwarded-Method", "GET");
        request.Headers.Add("Cookie", $"{AuthSessionService.AccessCookieName}={accessToken}");
        return request;
    }

    private static async Task<string> ContinueUrlOf(HttpResponseMessage response) {
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("continueUrl").GetString()!;
    }

    private static string SetCookie(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Set-Cookie"));

    private static async Task<int> AdminIdAsync(WatchtowerApiFactory factory) {
        var id = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            id = await db.Users
                .Where(u => u.UserName == AuthBootstrapService.AdminUserName)
                .Select(u => u.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
        });
        return id;
    }
}
