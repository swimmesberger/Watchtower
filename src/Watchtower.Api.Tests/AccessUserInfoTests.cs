using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The OIDC UserInfo endpoint (OpenID Connect Core 1.0 §5.3, docs/central-auth/design.md §5.3): the
/// standards-based way an app fetches rich identity on demand. It accepts either the assertion it was
/// forwarded (as a bearer token) or the browser's <c>__wt_access</c> cookie, and answers with OIDC-standard
/// claims — but only for a credential that still resolves to a live, enabled account, checked fresh.
/// </summary>
public sealed class AccessUserInfoTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AppDomain = "app.example.invalid";
    private const string ApiPath = "/api/access/userinfo";
    private const string AppPath = "/.watchtower/userinfo";

    private static (string Key, string? Value)[] AuthOn() =>
        [("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost)];

    [Fact]
    public async Task Bearer_ReturnsOidcClaims() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");

        var response = await client.SendAsync(WithBearer(ApiPath, await BearerForAsync(factory, userId)), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var doc = await ReadJson(response);
        var root = doc.RootElement;
        Assert.Equal(userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            root.GetProperty("sub").GetString());
        Assert.Equal("alice", root.GetProperty("preferred_username").GetString());
        Assert.Equal("alice@example.invalid", root.GetProperty("email").GetString());
        // In no group — but the claim is still stated, as an empty array. "This account has no groups" and
        // "this deployment does not answer group questions" are different facts, and an app mapping groups
        // onto roles has to be able to tell them apart.
        Assert.Empty(root.GetProperty("groups").EnumerateArray());
        // Not an admin, and we do not claim to verify email: those keys are absent.
        Assert.False(root.TryGetProperty("roles", out _));
        Assert.False(root.TryGetProperty("email_verified", out _));
    }

    [Fact]
    public async Task Bearer_ForAnAdmin_IncludesTheRole() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var userId = await factory.AddUserAsync("root");
        await SetAdminAsync(factory, userId);

        var response = await client.SendAsync(WithBearer(ApiPath, await BearerForAsync(factory, userId)), Ct);

        using var doc = await ReadJson(response);
        var roles = doc.RootElement.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal([WatchtowerClaims.AdminRole], roles);
        // An account with no email still omits the claim rather than sending it empty or null.
        Assert.False(doc.RootElement.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Bearer_ReturnsTheGroupsTheAccountIsIn_SortedAndFresh() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var userId = await factory.AddUserAsync("alice");
        // Created out of order on purpose — the answer must be the sort, not the insertion order.
        await factory.AddGroupAsync("viewers", userId);
        var admins = await factory.AddGroupAsync("admins", userId);
        var bearer = await BearerForAsync(factory, userId);

        using (var doc = await ReadJson(await client.SendAsync(WithBearer(ApiPath, bearer), Ct))) {
            Assert.Equal(["admins", "viewers"], Groups(doc));
        }

        await factory.RemoveFromGroupAsync(admins, userId);

        // Identity is answered as of now, not as of when the assertion was minted: the same bearer token
        // returns the reduced membership, which is what makes UserInfo the on-demand channel.
        using (var doc = await ReadJson(await client.SendAsync(WithBearer(ApiPath, bearer), Ct))) {
            Assert.Equal(["viewers"], Groups(doc));
        }
    }

    private static IReadOnlyList<string> Groups(JsonDocument doc) =>
        [.. doc.RootElement.GetProperty("groups").EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public async Task Cookie_OnTheAppDomainPath_ReturnsClaims() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");
        var token = await factory.AppSessionAsync(userId, routeId);

        // The browser same-origin path: /.watchtower/* is proxied to Watchtower, carrying the app's cookie.
        var request = new HttpRequestMessage(HttpMethod.Get, AppPath);
        request.Headers.Add("Cookie", $"{AuthSessionService.AccessCookieName}={token}");
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJson(response);
        Assert.Equal("alice", doc.RootElement.GetProperty("preferred_username").GetString());
    }

    [Fact]
    public async Task NoCredential_Is401WithABearerChallenge() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();

        var response = await client.GetAsync(ApiPath, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // RFC 6750 / OIDC shape.
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGarbageBearer_Is401() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(WithBearer(ApiPath, "not-a-real-token"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ABearerForADisabledAccount_Is401_EvenThoughTheTokenIsStillWithinItsWindow() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");
        var bearer = await BearerForAsync(factory, userId);

        // The assertion is minted, then the account is disabled seconds later. Identity is answered as of now,
        // not as of mint time, so a token that verifies cryptographically still returns nothing.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithBearer(ApiPath, bearer), Ct)).StatusCode);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var user = await db.Users.FindAsync([userId], Ct);
            user!.Disabled = true;
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithBearer(ApiPath, bearer), Ct)).StatusCode);
    }

    [Fact]
    public async Task ACookieForADisabledAccount_Is401() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var routeId = await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated);
        var userId = await factory.AddUserAsync("alice");
        var token = await factory.AppSessionAsync(userId, routeId);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var user = await db.Users.FindAsync([userId], Ct);
            user!.Disabled = true;
            await db.SaveChangesAsync(Ct);
        });

        var request = new HttpRequestMessage(HttpMethod.Get, AppPath);
        request.Headers.Add("Cookie", $"{AuthSessionService.AccessCookieName}={token}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task BothMountPoints_AnswerTheSameHandler() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var userId = await factory.AddUserAsync("alice");
        var bearer = await BearerForAsync(factory, userId);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithBearer(ApiPath, bearer), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithBearer(AppPath, bearer), Ct)).StatusCode);
    }

    [Fact]
    public async Task WithAuthDisabled_BothPathsAre404() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ApiPath, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(AppPath, Ct)).StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage WithBearer(string path, string token) {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>Mints the assertion an app would have been forwarded for <paramref name="userId"/>.</summary>
    private static async Task<string> BearerForAsync(WatchtowerApiFactory factory, int userId) {
        var token = string.Empty;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var signer = sp.GetRequiredService<AuthTokenSigner>();
            var user = await db.Users.SingleAsync(u => u.Id == userId, Ct);
            // For the account's own realm, which is what verify would have minted it for.
            var realm = await db.Realms.SingleAsync(r => r.Id == user.RealmId, Ct);
            token = signer.Mint(user, AppDomain, RealmIdentity.From(realm));
        });
        return token;
    }

    private static Task SetAdminAsync(WatchtowerApiFactory factory, int userId) =>
        factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var user = await db.Users.FindAsync([userId], Ct);
            user!.IsAdmin = true;
            await db.SaveChangesAsync(Ct);
        });

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
}
