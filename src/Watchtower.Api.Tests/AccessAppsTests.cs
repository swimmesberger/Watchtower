using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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

        // Stack names chosen to sort the opposite way round from the domains, so the assertion below
        // distinguishes "sorted by name" from "sorted by domain" rather than agreeing with both.
        await factory.AddRouteAsync(
            "aaa.shop.example.invalid", AccessMode.Authenticated, templateId: shop, stackName: "Zephyr");
        var granted = await factory.AddRouteAsync(
            "mmm.shop.example.invalid", AccessMode.Restricted, templateId: shop, stackName: "Ledger");
        var viaGroup = await factory.AddRouteAsync(
            "zzz.shop.example.invalid", AccessMode.Restricted, templateId: shop, stackName: "Atlas");
        await factory.AddRouteAsync("nope.shop.example.invalid", AccessMode.Restricted, templateId: shop);
        // Another population's app, reachable by nobody in acme however this endpoint is asked.
        await factory.AddRouteAsync(OperatorApp, AccessMode.Authenticated);

        var carol = await factory.AddUserAsync("carol", realmId: acme);
        await factory.GrantAsync(granted, carol);
        var staff = await factory.AddGroupInRealmAsync("staff", acme, carol);
        await factory.GrantGroupAsync(viaGroup, staff);

        var response = await GetAsync(client, await factory.SsoSessionAsync(carol));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        // Per-account body: no shared cache between here and the browser may hand it to the next visitor.
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        using var doc = await ReadJson(response);
        // Sorted by name, not by insertion, id, or domain — the page must not reshuffle between loads.
        Assert.Equal(["Atlas", "Ledger", "Zephyr"], Names(doc));
        Assert.Equal(
            ["zzz.shop.example.invalid", "mmm.shop.example.invalid", "aaa.shop.example.invalid"],
            Domains(doc));
    }

    /// <summary>
    /// The portal names applications a visitor can be sent to. A <see cref="RouteTarget.Watchtower"/>
    /// route is not one of them (ADR-0021) — it is the page this list is being rendered on, and the
    /// realm's own login host at that.
    /// </summary>
    [Fact]
    public async Task WatchtowerRoutes_AreNotListedAsApplications() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);
        await factory.AddRouteAsync("one.shop.example.invalid", AccessMode.Authenticated, templateId: shop);
        // A second Watchtower hostname in the same realm, so the exclusion is about the target and not
        // about the login-route designation.
        await factory.AddWatchtowerRouteAsync("portal.acme.invalid", acme);

        var carol = await factory.AddUserAsync("carol", realmId: acme);

        using var doc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(carol)));

        Assert.Equal(["one.shop.example.invalid"], Domains(doc));
    }

    /// <summary>
    /// Alias domains — several names for one service — are one application. The portal says so, naming the
    /// canonical domain once, rather than claiming the visitor has three apps.
    /// </summary>
    [Fact]
    public async Task AliasDomains_CollapseToTheEntryPointsPrimary() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);
        var primary = await factory.AddRouteAsync(
            "one.shop.example.invalid", AccessMode.Authenticated, templateId: shop);
        await factory.AddStackRouteAsync(primary, "vanity.acme.invalid");
        await factory.AddStackRouteAsync(primary, "legacy.acme.invalid");

        var carol = await factory.AddUserAsync("carol", realmId: acme);

        using var doc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(carol)));

        Assert.Equal(["one.shop.example.invalid"], Domains(doc));
    }

    /// <summary>
    /// A second <em>service</em> of one stack is not an alias: a UI and its API are two ways in, and
    /// collapsing them would hide one the caller may be the only person granted. So the grouping is by
    /// stack <em>and</em> service — aliases within each still collapse.
    /// </summary>
    [Fact]
    public async Task TwoServicesOfOneStack_AreTwoEntryPoints() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);
        // The stack's canonical domain, on the "web" service AddRouteAsync gives it.
        var ui = await factory.AddRouteAsync(
            "app.shop.example.invalid", AccessMode.Authenticated, templateId: shop);
        // A second name for that same service — still one entry point.
        await factory.AddStackRouteAsync(ui, "vanity.acme.invalid");
        // …and a different service of the same stack, which is a second one.
        await factory.AddStackRouteAsync(ui, "api.shop.example.invalid", serviceName: "api");

        var carol = await factory.AddUserAsync("carol", realmId: acme);

        using var doc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(carol)));

        // Two cards, not one and not three. Both carry the stack's name, so the domain beneath is what
        // tells them apart — which is what the card renders.
        Assert.Equal(["api.shop.example.invalid", "app.shop.example.invalid"], Domains(doc));
    }

    /// <summary>
    /// …but the canonical domain is only preferred where the caller can actually reach it. A Restricted
    /// estate may grant somebody an alias and not the primary, and dropping the entry point then would hide
    /// an application they hold a grant for. Showing what they can reach is the honest degradation.
    /// </summary>
    [Fact]
    public async Task AnAliasWithoutTheEntryPointsPrimary_IsStillListed() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);
        var primary = await factory.AddRouteAsync(
            "one.shop.example.invalid", AccessMode.Restricted, templateId: shop);
        var alias = await factory.AddStackRouteAsync(primary, "vanity.acme.invalid");

        var carol = await factory.AddUserAsync("carol", realmId: acme);
        await factory.GrantAsync(alias, carol);

        using var doc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(carol)));

        Assert.Equal(["vanity.acme.invalid"], Domains(doc));
    }

    /// <summary>
    /// The link the page follows carries the route's own scheme. A plain-HTTP route linked as
    /// <c>https</c> would simply fail to connect, and the browser has nothing to derive the answer from.
    /// </summary>
    [Fact]
    public async Task TheLinkFollowsTheRoutesOwnScheme() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);
        await factory.AddRouteAsync("secure.shop.example.invalid", AccessMode.Authenticated, templateId: shop);
        await factory.AddRouteAsync(
            "plain.shop.example.invalid", AccessMode.Authenticated, templateId: shop, tlsEnabled: false);

        var carol = await factory.AddUserAsync("carol", realmId: acme);

        using var doc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(carol)));

        Assert.Equal(
            ["http://plain.shop.example.invalid/", "https://secure.shop.example.invalid/"],
            Urls(doc));
    }

    /// <summary>
    /// The SSO cookie must hold an SSO session. An <c>__wt_access</c> token names the same account, so
    /// accepting one would not be an escalation — but it is a session minted for one app's domain, and the
    /// endpoint that says which apps you may enter is not the place to start treating the two as
    /// interchangeable.
    /// </summary>
    [Fact]
    public async Task AnAppSessionTokenInTheSsoCookie_Is401() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);
        var routeId = await factory.AddRouteAsync(
            "one.shop.example.invalid", AccessMode.Authenticated, templateId: shop);
        var carol = await factory.AddUserAsync("carol", realmId: acme);

        var appToken = await factory.AppSessionAsync(carol, routeId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, appToken)).StatusCode);
    }

    /// <summary>
    /// The endpoint renews the sliding window, and deliberately: it is served on the auth host, where
    /// <c>UseAuthentication</c> resolves the same <c>__wt_sso</c> cookie through the same renewing
    /// <c>ValidateAsync</c> before any endpoint runs. A non-renewing read in the handler could not have
    /// stopped that; it would only have made the code claim a property the pipeline does not have. Pinned
    /// so the next reader who reaches for <c>ValidateAnyAsync</c> here finds out why it would be theatre.
    /// </summary>
    [Fact]
    public async Task ListingApps_RenewsTheSession_BecauseAuthenticationAlreadyDid() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var carol = await factory.AddUserAsync("carol", realmId: acme);
        var token = await factory.SsoSessionAsync(carol);

        // Well inside the half-a-sliding-window renewal threshold (the default lifetime is hours), which is
        // the only state in which renewal is observable at all.
        await factory.SetSessionExpiryAsync(token, DateTimeOffset.UtcNow.AddMinutes(5));
        var before = await factory.SessionExpiryAsync(token);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, token)).StatusCode);
        Assert.True(await factory.SessionExpiryAsync(token) > before);
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
    /// A public route asks nobody who they are, so the access policy admits everyone to it — right for "may
    /// this request pass", and wrong for "what shall I name to you". The realm filter is what settles the
    /// difference: the caller's own realm's public app is listed, another realm's is not. Without it this
    /// endpoint would hand any account of any population every public domain the instance proxies, which is
    /// the enumeration <c>/api/proxy/ask</c> already answers 404 to prevent on these same hosts.
    /// </summary>
    [Fact]
    public async Task APublicRoute_IsListedOnlyToItsOwnRealm() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme", AcmeAuthHost);
        var shop = await factory.AddTemplateAsync("shop", acme);
        await factory.AddRouteAsync("open.shop.example.invalid", AccessMode.Public, templateId: shop);
        await factory.AddRouteAsync(OperatorApp, AccessMode.Public);

        var carol = await factory.AddUserAsync("carol", realmId: acme);
        var alice = await factory.AddUserAsync("alice");

        using var realmDoc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(carol)));
        using var operatorDoc = await ReadJson(await GetAsync(client, await factory.SsoSessionAsync(alice)));

        Assert.Equal(["open.shop.example.invalid"], Domains(realmDoc));
        Assert.Equal([OperatorApp], Domains(operatorDoc));
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

    private static IReadOnlyList<string> Urls(JsonDocument doc) =>
        [.. doc.RootElement.GetProperty("apps").EnumerateArray().Select(e => e.GetProperty("url").GetString()!)];

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
}
