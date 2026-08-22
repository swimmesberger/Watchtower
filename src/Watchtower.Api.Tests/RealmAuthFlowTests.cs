using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The auth flows once there is more than one population (docs/central-auth/design.md §13), driven through
/// the real host: which login page a protected route sends a visitor to, which accounts a login page can
/// authenticate, what a wrong-realm account gets, and what the assertion a realm's app receives says.
/// </summary>
public sealed class RealmAuthFlowTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AcmeAuthHost = "login.acme.invalid";
    private const string AcmeApp = "one.shop.example.invalid";
    private const string OperatorApp = "app.example.invalid";
    private const string HtmlAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string Password = "correct-horse-battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Key, string? Value)[] AuthOn(params (string Key, string? Value)[] extra) =>
        [("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost), .. extra];

    // ── Challenge: which login page ───────────────────────────────────────────

    [Fact]
    public async Task AnonymousNavigation_IsSentToTheRoutesOwnRealmLogin() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var template = await factory.AddTemplateAsync("shop", acme);
        await factory.AddRouteAsync(AcmeApp, AccessMode.Authenticated, templateId: template);
        await factory.AddRouteAsync(OperatorApp, AccessMode.Authenticated);

        var toRealm = await Send(client, Verify(AcmeApp, "/reports", accept: HtmlAccept));
        var toOperator = await Send(client, Verify(OperatorApp, "/reports", accept: HtmlAccept));

        // Each visitor is offered the login of the population that could actually admit them; sending them
        // to the operator page would be a prompt nobody in that realm can satisfy.
        Assert.Equal(HttpStatusCode.Redirect, toRealm.StatusCode);
        Assert.Equal($"https://{AcmeAuthHost}/login", toRealm.Headers.Location!.GetLeftPart(UriPartial.Path));
        Assert.Equal(HttpStatusCode.Redirect, toOperator.StatusCode);
        Assert.Equal($"https://{AuthHost}/login", toOperator.Headers.Location!.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task ARealmWithNoAuthHost_FailsClosed() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme");
        var template = await factory.AddTemplateAsync("shop", acme);
        await factory.AddRouteAsync(AcmeApp, AccessMode.Authenticated, templateId: template);

        var response = await Send(client, Verify(AcmeApp, "/reports", accept: HtmlAccept));

        // A realm created before its DNS exists has nowhere to send anyone, and guessing would mean
        // inventing a redirect target — the same answer an instance with no Auth:Host already gives.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    // ── Access: the realm invariant, end to end ───────────────────────────────

    [Fact]
    public async Task AnOperatorAccount_CannotEnterARealmsApp() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var template = await factory.AddTemplateAsync("shop", acme);
        var routeId = await factory.AddRouteAsync(AcmeApp, AccessMode.Authenticated, templateId: template);

        var alice = await factory.AddUserAsync("alice");
        // A session for the right route, held by an account of the wrong population. Only reachable as a
        // leftover — the callback that mints app sessions runs the same authorisation check — which is
        // exactly why verify re-runs it on every request rather than trusting the cookie.
        var token = await factory.AppSessionAsync(alice, routeId);

        var response = await Send(client, Verify(AcmeApp, "/", cookie: AccessCookie(token), accept: HtmlAccept));

        // The signed-in-but-not-permitted answer, identical to the one a missing grant produces: a realm
        // mismatch is not a distinguishable outcome.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ARealmAccount_EntersItsOwnApp_AndGetsARealmScopedAssertion() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var template = await factory.AddTemplateAsync("shop", acme);
        var routeId = await factory.AddRouteAsync(AcmeApp, AccessMode.Authenticated, templateId: template);

        var carol = await factory.AddUserAsync("carol", realmId: acme);
        var token = await factory.AppSessionAsync(carol, routeId);

        var response = await Send(client, Verify(AcmeApp, "/", cookie: AccessCookie(token)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jwt = new JsonWebToken(Assert.Single(response.Headers.GetValues(RouteAccessPolicy.JwtHeaderName)));
        // The realm's own login host is its issuer, and the population is stated outright.
        Assert.Equal(AcmeAuthHost, jwt.Issuer);
        Assert.Equal("acme", jwt.GetClaim("realm").Value);
        Assert.Equal(AcmeApp, Assert.Single(jwt.Audiences));
    }

    [Fact]
    public async Task AnOperatorAccount_KeepsTodaysAssertionExactly() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(OperatorApp, AccessMode.Authenticated);
        var alice = await factory.AddUserAsync("alice");
        var token = await factory.AppSessionAsync(alice, routeId);

        var response = await Send(client, Verify(OperatorApp, "/", cookie: AccessCookie(token)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jwt = new JsonWebToken(Assert.Single(response.Headers.GetValues(RouteAccessPolicy.JwtHeaderName)));
        Assert.Equal(AuthHost, jwt.Issuer);
        Assert.Equal(Realm.SystemRealmSlug, jwt.GetClaim("realm").Value);
    }

    [Fact]
    public async Task ARestrictedRouteRefusesAWrongRealmGrant_AtAccessTime() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var template = await factory.AddTemplateAsync("shop", acme);
        var routeId = await factory.AddRouteAsync(AcmeApp, AccessMode.Restricted, templateId: template);

        var alice = await factory.AddUserAsync("alice");
        // Written straight into the table, the way a grant predating a realm change would sit there.
        await factory.GrantAsync(routeId, alice);
        var token = await factory.AppSessionAsync(alice, routeId);

        var response = await Send(client, Verify(AcmeApp, "/", cookie: AccessCookie(token), accept: HtmlAccept));

        // The grant exists and names this exact account; the realm predicate sits in front of it, so the
        // row admits nobody. Same 403 as an account holding no grant at all.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Login: one credential space per host ──────────────────────────────────

    [Fact]
    public async Task LoginOnARealmHost_OnlySeesThatRealmsAccounts() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        await factory.AddUserAsync("carol", realmId: acme);

        // The operator population has its own `admin`, bootstrapped on first start.
        var asRealm = await LoginAsync(client, AcmeAuthHost, "carol", Password);
        var operatorOnRealmHost = await LoginAsync(client, AcmeAuthHost, "admin", WatchtowerApiFactory.AdminPassword);
        var realmOnOperatorHost = await LoginAsync(client, AuthHost, "carol", Password);

        Assert.Equal(HttpStatusCode.OK, asRealm.StatusCode);
        // The host is the cookie jar, so it is also the credential space: neither account exists on the
        // other's login page, and both refusals are the same indistinguishable one.
        Assert.Equal(HttpStatusCode.Unauthorized, operatorOnRealmHost.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, realmOnOperatorHost.StatusCode);
    }

    [Fact]
    public async Task TheSameNameInTwoRealms_AuthenticatesAgainstItsOwnPassword() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        // A distinct password, so what the last assertion proves is the account and not merely the name.
        const string realmPassword = "a-different-passphrase";
        await factory.AddUserAsync("admin", realmId: acme, password: realmPassword);

        var realmAdmin = await LoginAsync(client, AcmeAuthHost, "admin", realmPassword);
        var operatorAdmin = await LoginAsync(client, AuthHost, "admin", WatchtowerApiFactory.AdminPassword);
        // The realm account's password on the operator page: same login name, different population.
        var crossed = await LoginAsync(client, AuthHost, "admin", realmPassword);

        Assert.Equal(HttpStatusCode.OK, realmAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, operatorAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, crossed.StatusCode);
    }

    // ── UserInfo: no realm in context, so the issuer decides ──────────────────

    [Fact]
    public async Task UserInfo_RefusesAnAssertionWhoseIssuerIsNotTheSubjectsRealm() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var alice = await factory.AddUserAsync("alice");
        var carol = await factory.AddUserAsync("carol", realmId: acme);

        var forCarol = await MintAsync(factory, carol, acme);
        var mismatched = await MintAsync(factory, alice, acme);

        Assert.Equal(HttpStatusCode.OK, (await UserInfoAsync(client, forCarol)).StatusCode);
        // Same key, same issuer, but the subject belongs to the operator realm — one key pair signs every
        // realm, so the account has to be re-checked rather than inferred from the issuer.
        Assert.Equal(HttpStatusCode.Unauthorized, (await UserInfoAsync(client, mismatched)).StatusCode);
    }

    /// <summary>
    /// UserInfo describes the same account the login principal does, so it applies the same rule: the Admin
    /// role is only ever reported for an operator-realm account. The handlers refuse to set the flag outside
    /// that realm, so this is the belt to those braces — a row that acquired it by a direct database edit
    /// must not be described to an application as holding instance-wide authority.
    /// </summary>
    [Fact]
    public async Task UserInfo_NeverReportsTheAdminRoleForARealmAccount() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var carol = await factory.AddUserAsync("carol", realmId: acme);
        var alice = await factory.AddUserAsync("alice");
        await SetAdminAsync(factory, carol);
        await SetAdminAsync(factory, alice);

        var realmBody = await BodyAsync(await UserInfoAsync(client, await MintAsync(factory, carol, acme)));
        var operatorBody = await BodyAsync(
            await UserInfoAsync(client, await MintAsync(factory, alice, Realm.SystemRealmId)));

        Assert.DoesNotContain("\"roles\"", realmBody, StringComparison.Ordinal);
        Assert.Contains("\"roles\"", operatorBody, StringComparison.Ordinal);
    }

    // ── The management surface is the operator population's ───────────────────

    /// <summary>
    /// End to end over the transport that matters: a realm account holds a valid central session — the
    /// same one that lets it into its own applications — and every JSON-RPC call it makes is refused.
    /// </summary>
    [Fact]
    public async Task ARealmAccount_HoldsAValidSession_AndIsStillForbiddenOnRpc() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var carol = await factory.AddUserAsync("carol", realmId: acme);
        var carolCookie = SsoCookie(await factory.SsoSessionAsync(carol));

        var operatorAdmin = await AdminIdAsync(factory);
        var adminCookie = SsoCookie(await factory.SsoSessionAsync(operatorAdmin));

        var asRealm = await RpcAsync(client, "credentials.list", carolCookie);
        var asOperator = await RpcAsync(client, "credentials.list", adminCookie);

        // -32003 is the JSON-RPC code the transport maps Forbidden to. The session itself is fine — the
        // bootstrap below reports it as authenticated — so what refuses the call is the realm and nothing
        // else.
        Assert.Contains("-32003", asRealm, StringComparison.Ordinal);
        Assert.Contains("\"result\"", asOperator, StringComparison.Ordinal);

        var bootstrap = await RpcAsync(client, "elarion.session", carolCookie);
        Assert.Contains("\"isAuthenticated\":true", bootstrap, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same boundary on the surfaces ASP.NET's authorization middleware decides rather than Elarion's
    /// handler pipeline. The two SSE streams are minimal-API endpoints, so nothing in the handler pipeline
    /// runs for them: with a bare <c>RequireAuthorization()</c> a customer realm's account holding a
    /// perfectly valid session on its own login host could stream deploy output and <em>any</em>
    /// container's logs.
    /// </summary>
    [Theory]
    [InlineData("/api/stacks/events/1/stream")]
    [InlineData("/api/containers/abc/logs")]
    public async Task ARealmAccount_IsForbiddenOnTheStreamEndpoints(string path) {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var carol = await factory.AddUserAsync("carol", realmId: acme);

        var authenticated = await GetAsync(client, path, SsoCookie(await factory.SsoSessionAsync(carol)));
        var anonymous = await GetAsync(client, path, cookie: null);

        // 403, not 401: the session is real and the middleware says so — what it lacks is the realm.
        Assert.Equal(HttpStatusCode.Forbidden, authenticated.StatusCode);
        // And an anonymous caller still gets the challenge it always did, rather than being told a session
        // would not have helped.
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task AnOperatorAccount_StillReachesTheStreamEndpoints_WithoutBeingAnAdministrator() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        // Deliberately not an administrator: this is a realm boundary, not a re-grading of who may watch
        // deploy output — a plain operator account could before realms existed and still can.
        var bob = await factory.AddUserAsync("bob");

        var response = await GetAsync(
            client, "/api/stacks/events/1/stream", SsoCookie(await factory.SsoSessionAsync(bob)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? cookie) {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request, Ct);
    }


    /// <summary>The id of the bootstrapped operator administrator.</summary>
    private static async Task<int> AdminIdAsync(WatchtowerApiFactory factory) {
        var id = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            id = (await db.Users.SingleAsync(u => u.NormalizedUserName == "ADMIN", Ct)).Id;
        });
        return id;
    }

    private static async Task<string> RpcAsync(HttpClient client, string method, string cookie) {
        var request = new HttpRequestMessage(HttpMethod.Post, "/rpc") {
            Content = JsonContent.Create(new { jsonrpc = "2.0", method, @params = new { }, id = "1" }),
        };
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, Ct);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static string SsoCookie(string token) => $"{AuthSessionService.SsoCookieName}={token}";


    /// <summary>Mints the assertion verify would have written, for <paramref name="realmId"/>.</summary>
    private static async Task<string> MintAsync(WatchtowerApiFactory factory, int userId, int realmId) {
        var token = string.Empty;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var signer = sp.GetRequiredService<AuthTokenSigner>();
            var user = await db.Users.SingleAsync(u => u.Id == userId, Ct);
            var realm = await db.Realms.SingleAsync(r => r.Id == realmId, Ct);
            var realms = sp.GetRequiredService<RealmResolver>();
            token = signer.Mint(user, AcmeApp, await realms.IdentityForAsync(realm, Ct));
        });
        return token;
    }

    /// <summary>Sets the flag directly — the handlers refuse to for a realm account, which is the point.</summary>
    private static Task SetAdminAsync(WatchtowerApiFactory factory, int userId) =>
        factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId, Ct);
            user.IsAdmin = true;
            await db.SaveChangesAsync(Ct);
        });

    private static Task<string> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(Ct);

    private static Task<HttpResponseMessage> UserInfoAsync(HttpClient client, string bearer) {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/access/userinfo");
        request.Headers.Add("Authorization", $"Bearer {bearer}");
        return client.SendAsync(request, Ct);
    }

    /// <summary>Posts the login form as it arrives on <paramref name="host"/> — the realm's own login page.</summary>
    private static Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string host, string userName, string password) {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") {
            Content = JsonContent.Create(new { userName, password }),
        };
        request.Headers.Host = host;
        return client.SendAsync(request, Ct);
    }

    /// <summary>Builds the request Caddy's <c>forward_auth</c> makes for one original request.</summary>
    private static HttpRequestMessage Verify(
        string host, string uri = "/", string? cookie = null, string? accept = null) {
        var request = new HttpRequestMessage(HttpMethod.Get, RouteAccessPolicy.VerifyPath);
        request.Headers.Add("X-Forwarded-Host", host);
        request.Headers.Add("X-Forwarded-Uri", uri);
        request.Headers.Add("X-Forwarded-Method", "GET");
        if (accept is not null) request.Headers.Add("Accept", accept);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static string AccessCookie(string token) => $"{AuthSessionService.AccessCookieName}={token}";

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpRequestMessage request) =>
        client.SendAsync(request, Ct);
}
