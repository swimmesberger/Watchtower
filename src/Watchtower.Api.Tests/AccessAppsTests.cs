using System.Net;
using System.Text.Json;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// <c>GET /api/access/apps</c> — the applications list behind the SPA's "Your applications" landing page
/// (docs/central-auth/design.md §13). It is the any-realm surface, so what it must get right is that the
/// answer is exactly what the caller could already reach: their own realm's routes, and only the ones the
/// route access policy admits them to.
/// </summary>
public sealed class AccessAppsTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AcmeAuthHost = "login.acme.invalid";
    private const string OperatorApp = "office.example.invalid";
    private const string ApiPath = "/api/access/apps";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Key, string? Value)[] AuthOn() =>
        [("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost)];

    /// <summary>
    /// The whole point of the endpoint in one test: a realm account is told about its own applications,
    /// whether the grant naming it is direct or comes through a group, and about nothing else — not the
    /// route it holds no grant for, and not another population's route at all.
    /// </summary>
    [Fact]
    public async Task ARealmAccount_SeesExactlyTheAppsItMayEnter() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);

        await factory.AddRouteAsync("one.shop.example.invalid", AccessMode.Authenticated, templateId: shop);
        var granted = await factory.AddRouteAsync("two.shop.example.invalid", AccessMode.Restricted, templateId: shop);
        var viaGroup = await factory.AddRouteAsync("three.shop.example.invalid", AccessMode.Restricted, templateId: shop);
        await factory.AddRouteAsync("four.shop.example.invalid", AccessMode.Restricted, templateId: shop);
        // Another population's app, reachable by nobody in acme however this endpoint is asked.
        await factory.AddRouteAsync(OperatorApp, AccessMode.Authenticated);

        var carol = await factory.AddUserAsync("carol", realmId: acme);
        await factory.GrantAsync(granted, carol);
        var staff = await factory.AddGroupInRealmAsync("staff", acme, carol);
        await factory.GrantGroupAsync(viaGroup, staff);

        var response = await GetAsync(client, await factory.SsoSessionAsync(carol));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = await ReadJson(response);
        Assert.Equal(
            ["one.shop.example.invalid", "three.shop.example.invalid", "two.shop.example.invalid"],
            Domains(doc));
        // Sorted by name, not by insertion or id — the page must not reshuffle between loads. The label is
        // the stack's name, which for a tenant is the one in the visitor's own address bar.
        Assert.Equal(["one", "three", "two"], Names(doc));
    }

    /// <summary>
    /// The realm invariant, seen from this endpoint: an operator account is answered about the operator
    /// realm's routes and no others, exactly as verify would answer it. The endpoint works for any realm —
    /// including the system one — because it asks the same policy rather than a management-shaped one.
    /// </summary>
    [Fact]
    public async Task AnOperatorAccount_SeesTheOperatorRealmsApps_AndNotARealms() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);
        await factory.AddRouteAsync("one.shop.example.invalid", AccessMode.Authenticated, templateId: shop);
        await factory.AddRouteAsync(OperatorApp, AccessMode.Authenticated);

        var alice = await factory.AddUserAsync("alice");

        using var doc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(alice)));

        Assert.Equal([OperatorApp], Domains(doc));
    }

    /// <summary>
    /// A grant naming a foreign account is not a special case here either: the realm predicate sits in
    /// front of it, so the row lists nothing rather than crossing the boundary.
    /// </summary>
    [Fact]
    public async Task AWrongRealmGrant_ListsNothing() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);
        var routeId = await factory.AddRouteAsync("one.shop.example.invalid", AccessMode.Restricted, templateId: shop);

        var alice = await factory.AddUserAsync("alice");
        // Written straight into the table, the way a grant predating a realm change would sit there.
        await factory.GrantAsync(routeId, alice);

        using var doc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(alice)));

        Assert.Empty(Domains(doc));
    }

    /// <summary>
    /// A public route is listed for everyone, because the policy this endpoint asks says everyone may enter
    /// it — it is served to anonymous visitors without so much as a verify call. Naming it therefore
    /// discloses nothing: the alternative would be a second, different reading of accessibility, which is
    /// the thing the design refuses to have.
    /// </summary>
    [Fact]
    public async Task APublicRoute_IsListedForEveryRealm() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        await factory.AddRouteAsync(OperatorApp, AccessMode.Public);
        var carol = await factory.AddUserAsync("carol", realmId: acme);

        using var doc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(carol)));

        Assert.Equal([OperatorApp], Domains(doc));
    }

    [Fact]
    public async Task AnAccountWithNoApps_GetsAnEmptyList() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var carol = await factory.AddUserAsync("carol", realmId: acme);

        var response = await GetAsync(client, await factory.SsoSessionAsync(carol));

        // The list is stated, empty, rather than 404: "you may enter nothing yet" is an answer the portal
        // renders, and it is different from "there is no such endpoint".
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJson(response);
        Assert.Empty(Domains(doc));
    }

    [Fact]
    public async Task NoSession_Is401() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ApiPath, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "not-a-real-token")).StatusCode);
    }

    [Fact]
    public async Task ADisabledAccountsSession_Is401() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var carol = await factory.AddUserAsync("carol", realmId: acme, disabled: true);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await GetAsync(client, await factory.SsoSessionAsync(carol))).StatusCode);
    }

    [Fact]
    public async Task WithAuthDisabled_Is404() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();

        // The implicit-admin deployment has no login and therefore no portal — and 404 beats the SPA
        // fallback's index.html-with-a-200, which is a confusing answer to a fetch.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ApiPath, Ct)).StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string ssoToken) {
        var request = new HttpRequestMessage(HttpMethod.Get, ApiPath);
        request.Headers.Add("Cookie", $"{AuthSessionService.SsoCookieName}={ssoToken}");
        return client.SendAsync(request, Ct);
    }

    private static IReadOnlyList<string> Domains(JsonDocument doc) =>
        [.. doc.RootElement.GetProperty("apps").EnumerateArray().Select(e => e.GetProperty("domain").GetString()!)];

    private static IReadOnlyList<string> Names(JsonDocument doc) =>
        [.. doc.RootElement.GetProperty("apps").EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
}
