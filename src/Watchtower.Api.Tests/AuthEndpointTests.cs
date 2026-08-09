using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Covers <c>/api/auth/login</c> through the real pipeline: the properties that make it safe are the
/// ones a unit test of the handler body cannot see — what the lockout actually does to the row, whether
/// two different failures are distinguishable on the wire, and which attributes end up on the cookie.
/// </summary>
public sealed class AuthEndpointTests {
    private static (string Key, string? Value)[] AuthOn(params (string Key, string? Value)[] extra) =>
        [("Watchtower:Auth:Enabled", "true"), .. extra];

    private static HttpRequestMessage Login(string userName, string password, string? forwardedProto = null) {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") {
            Content = JsonContent.Create(new { userName, password }),
        };
        if (forwardedProto is not null) request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        return request;
    }

    [Fact]
    public async Task LockedAccount_RefusesWithoutExtendingTheLockout() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        // Five failures is the configured threshold.
        for (var attempt = 0; attempt < 5; attempt++) {
            var failed = await client.SendAsync(Login("admin", "wrong-password"), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var lockedAt = await ReadLockoutEndAsync(factory);
        Assert.NotNull(lockedAt);

        // A sixth attempt must be refused, but must NOT push the lockout further out — otherwise anyone
        // who can keep guessing can keep a legitimate operator locked out indefinitely.
        var sixth = await client.SendAsync(Login("admin", "wrong-password"), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, sixth.StatusCode);
        Assert.Equal(lockedAt, await ReadLockoutEndAsync(factory));

        // And the correct password is refused too, for as long as the lockout stands.
        var correct = await client.SendAsync(Login("admin", WatchtowerApiFactory.AdminPassword), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
        Assert.Equal(lockedAt, await ReadLockoutEndAsync(factory));
    }

    [Fact]
    public async Task UnknownUserAndWrongPassword_AreIndistinguishableOnTheWire() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        var unknown = await client.SendAsync(Login("no-such-account", "wrong-password"), ct);
        var wrongPassword = await client.SendAsync(Login("admin", "wrong-password"), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(wrongPassword.StatusCode, unknown.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(ct),
            await unknown.Content.ReadAsStringAsync(ct));

        // Nothing that names the account may leak through a header either (e.g. WWW-Authenticate).
        Assert.False(unknown.Headers.Contains("Set-Cookie"));
        Assert.Equal(
            wrongPassword.Headers.Select(h => h.Key).Order(StringComparer.Ordinal),
            unknown.Headers.Select(h => h.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cookie_IsHttpOnlyAndLaxAndNotSecureOverPlainHttp() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();

        var cookie = await SignInAndReadCookieAsync(client);

        Assert.Contains("__wt_sso=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        // Host-scoped: a Domain attribute would widen the cookie to sibling hosts.
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        // Auto over plain HTTP: marking it Secure here would set a cookie the browser never sends back,
        // which is exactly how the published-port recovery path would stop working.
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_IsSecure_WhenTheProxyReportsHttps() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();

        // The shipped topology: the browser spoke HTTPS to Caddy, Caddy speaks plain HTTP to Kestrel.
        // Without UseForwardedHeaders this cookie would never carry Secure in any real deployment.
        var cookie = await SignInAndReadCookieAsync(client, forwardedProto: "https");

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_IsSecure_WhenThePolicyIsAlways_EvenOverPlainHttp() {
        using var factory = new WatchtowerApiFactory(AuthOn(("Watchtower:Auth:CookieSecure", "Always")));
        using var client = factory.CreateApiClient();

        var cookie = await SignInAndReadCookieAsync(client);

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_IsNotSecure_WhenThePolicyIsNever_EvenBehindHttps() {
        using var factory = new WatchtowerApiFactory(AuthOn(("Watchtower:Auth:CookieSecure", "Never")));
        using var client = factory.CreateApiClient();

        var cookie = await SignInAndReadCookieAsync(client, forwardedProto: "https");

        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedInCookie_UnlocksTheRpcSurface_AndLogoutRevokesIt() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        var cookie = await SignInAndReadCookieAsync(client);
        var token = cookie.Split(';')[0];

        var authorized = await SendRpcAsync(client, "credentials.list", token);
        Assert.Contains("\"result\"", authorized, StringComparison.Ordinal);

        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Cookie", token);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout, ct)).StatusCode);

        // Sessions are database rows, so signing out invalidates the cookie immediately rather than
        // waiting for a self-contained ticket to expire.
        var afterLogout = await SendRpcAsync(client, "credentials.list", token);
        Assert.Contains("-32005", afterLogout, StringComparison.Ordinal);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.AuthSessions.AnyAsync(ct));
            // The trail records both ends of the session.
            var kinds = await db.AuthEvents.OrderBy(e => e.Id).Select(e => e.Kind).ToListAsync(ct);
            Assert.Equal(["login.ok", "logout"], kinds);
        });
    }

    [Fact]
    public async Task NonJsonContentType_IsRejectedBeforeAnyCredentialCheck() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        // A cross-site HTML form can only produce these content types; refusing them is what keeps a
        // forged POST from signing someone into an attacker's account.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") {
            Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("userName", "admin"),
                new KeyValuePair<string, string>("password", WatchtowerApiFactory.AdminPassword),
            ]),
        };

        var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static async Task<string> SignInAndReadCookieAsync(HttpClient client, string? forwardedProto = null) {
        var response = await client.SendAsync(
            Login("admin", WatchtowerApiFactory.AdminPassword, forwardedProto),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        return Assert.Single(cookies!);
    }

    private static async Task<string> SendRpcAsync(HttpClient client, string method, string? cookie) {
        var request = new HttpRequestMessage(HttpMethod.Post, "/rpc") {
            Content = JsonContent.Create(new { jsonrpc = "2.0", method, @params = new { }, id = "1" }),
        };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<DateTimeOffset?> ReadLockoutEndAsync(WatchtowerApiFactory factory) {
        DateTimeOffset? lockoutEnd = null;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            lockoutEnd = await db.Users
                .Where(u => u.UserName == AuthBootstrapService.AdminUserName)
                .Select(u => u.LockoutEnd)
                .SingleAsync(TestContext.Current.CancellationToken);
        });
        return lockoutEnd;
    }
}
