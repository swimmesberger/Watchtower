using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Covers the two user-scoped tenant-discovery endpoints against the real host pipeline:
/// <c>GET /api/app/tenants/accessible</c> (a tenant stack asking which siblings its visitor may switch to)
/// and <c>GET /api/mgmt/templates/{id}/tenants/accessible</c> (a vendor's management UI asking the same of
/// a template it has been granted).
/// </summary>
/// <remarks>
/// <para>
/// These are the only endpoints where a stack learns anything about another stack, so the tests here are
/// mostly about what does <em>not</em> come back. Three properties carry the design and each is pinned
/// below: the user is named by a Watchtower-signed assertion rather than by anything in the request; that
/// assertion is only accepted when its <c>aud</c> is one of the <em>calling</em> stack's own domains, so an
/// app can only ask about somebody actually visiting it; and every way of failing that check produces one
/// indistinguishable 401.
/// </para>
/// <para>
/// Expiry is not exercised here. The host's clock is the real one — the App API factory does not substitute
/// a <c>TimeProvider</c> — and, more to the point, every assertion failure funnels through a single branch,
/// so an expired token could not produce a different answer than the wrong-audience one already asserted.
/// The expiry check itself is covered where it lives, in <c>AuthTokenSignerTests</c>.
/// </para>
/// </remarks>
public sealed class TenantSwitcherTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AppPath = "/api/app/tenants/accessible";
    private const string CallerDomain = "acme.example.com";
    private const string ConsoleDomain = "console.example.com";

    private static WatchtowerApiFactory AuthOn() =>
        new(("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost));

    // ── GET /api/app/tenants/accessible ─────────────────────────────────────────

    [Fact]
    public async Task App_ListsTheSiblingsTheVisitorMayEnter_AndFlagsTheCallersOwnRow() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        using var client = factory.CreateApiClient();

        var response = await SendAsync(client, AppPath, estate.CallerToken,
            await AssertionAsync(factory, estate.UserId, CallerDomain));
        var raw = await BodyAsync(response);
        var tenants = JsonDocument.Parse(raw).RootElement.GetProperty("tenants").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Public and Authenticated siblings, plus the Restricted one this user holds a grant for. The
        // ungranted Restricted tenant, the tenant whose mode this build cannot read, the tenant with no
        // route to switch to, and the other template's tenant are all absent.
        // …by slug ascending, not in the order the rows were written: `abacus` was seeded last.
        Assert.Equal(["abacus", "acme", "globex", "vip"],
            tenants.Select(t => t.GetProperty("slug").GetString()));
        Assert.Equal("acme.example.com", tenants[1].GetProperty("domain").GetString());
        // `current` is true for exactly the stack answering the request.
        Assert.Equal([false, true, false, false],
            tenants.Select(t => t.GetProperty("current").GetBoolean()));

        // Nothing operational rides along: no stack ids, no deploy state, no environment values.
        Assert.DoesNotContain("stackId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastDeploy", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("wtapp_", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("elsewhere", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stack credential is still the first gate: a request carrying a perfectly good user assertion but
    /// no App API token is refused as an unauthenticated stack, and never reaches the assertion at all.
    /// </summary>
    [Fact]
    public async Task App_WithoutTheStackToken_Is401_WhateverTheAssertionSays() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        using var client = factory.CreateApiClient();

        var response = await SendAsync(client, AppPath, token: null,
            await AssertionAsync(factory, estate.UserId, CallerDomain));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Missing or invalid App API token.", await BodyAsync(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every way of failing the assertion check answers identically. Telling "no token" from "wrong
    /// audience" from "that account is disabled" is exactly the signal an enumeration attempt wants.
    /// </summary>
    [Fact]
    public async Task App_EveryAssertionFailure_IsTheSame401() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        var disabled = await factory.AddUserAsync("mallory", disabled: true);
        using var client = factory.CreateApiClient();

        var minted = await AssertionAsync(factory, estate.UserId, CallerDomain);
        var bodies = new List<string>();
        foreach (var assertion in new[] {
                     // Absent entirely.
                     null,
                     // Not a token at all.
                     "not-a-jwt",
                     // Cryptographically sound, but minted for a different app — the anti-enumeration case.
                     await AssertionAsync(factory, estate.UserId, "somewhere.else.example.com"),
                     // Ours, but tampered with.
                     minted[..^1] + (minted[^1] == 'A' ? 'B' : 'A'),
                     // Ours and current, for an account that is disabled.
                     await AssertionAsync(factory, disabled, CallerDomain),
                 }) {
            var response = await SendAsync(client, AppPath, estate.CallerToken, assertion);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            // Not the session challenge: this surface answers with its own body, never WWW-Authenticate.
            Assert.Empty(response.Headers.WwwAuthenticate);
            bodies.Add(await BodyAsync(response));
        }

        Assert.Contains("Missing or invalid user assertion.", bodies[0], StringComparison.Ordinal);
        Assert.All(bodies, body => Assert.Equal(bodies[0], body));
    }

    /// <summary>
    /// An assertion that was valid a moment ago stops being accepted the moment the account is disabled —
    /// the row is reloaded per request rather than trusted from mint time.
    /// </summary>
    [Fact]
    public async Task App_ForAnAccountDisabledAfterTheAssertionWasMinted_Is401() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        using var client = factory.CreateApiClient();
        var assertion = await AssertionAsync(factory, estate.UserId, CallerDomain);

        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(client, AppPath, estate.CallerToken, assertion)).StatusCode);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var user = await db.Users.FindAsync([estate.UserId], Ct);
            user!.Disabled = true;
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(client, AppPath, estate.CallerToken, assertion)).StatusCode);
    }

    /// <summary>
    /// A standalone stack has no siblings, and it already knows that about itself — so it is told plainly,
    /// before and regardless of any assertion.
    /// </summary>
    [Fact]
    public async Task App_FromAStackThatIsNotATenant_Is404() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        var (_, token) = await factory.AddCallerStackAsync("standalone", domain: "standalone.example.com");
        using var client = factory.CreateApiClient();

        var withAssertion = await SendAsync(client, AppPath, token,
            await AssertionAsync(factory, estate.UserId, "standalone.example.com"));
        var without = await SendAsync(client, AppPath, token, assertion: null);

        Assert.Equal(HttpStatusCode.NotFound, withAssertion.StatusCode);
        Assert.Contains("This stack is not a tenant of a template.",
            await BodyAsync(withAssertion), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, without.StatusCode);
    }

    /// <summary>
    /// A caller Watchtower serves no domain for has no audience to bind an assertion to, and "no audience"
    /// must never be read as "any audience" — an otherwise perfect assertion is refused on both surfaces.
    /// </summary>
    /// <remarks>
    /// This is the surface-level guard on the <c>db.Routes</c> → <c>TryValidate</c> wiring: were the
    /// audience set ever to fall back to every domain Watchtower knows (or to an empty set read as
    /// unconstrained), these two calls would start succeeding, and any stack could ask about any visitor.
    /// </remarks>
    [Fact]
    public async Task ACallerWithNoRouteDomains_Is401_HoweverGoodTheAssertionIs() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        // A tenant of the same template, provisioned but not yet routed.
        var routeless = await factory.AddTenantAsync(estate.TemplateId, "hermit", withRoute: false);
        // …and a granted management stack that has no domain of its own either.
        var (headlessId, headlessToken) = await factory.AddCallerStackAsync("headless-console");
        await factory.GrantManagementAsync(headlessId, estate.TemplateId);
        using var client = factory.CreateApiClient();

        // Current, correctly signed, for a live account, and minted for a domain this estate really serves
        // — just not one of *these* callers'.
        var assertion = await AssertionAsync(factory, estate.UserId, CallerDomain);
        var app = await SendAsync(client, AppPath, await factory.AppApiTokenAsync(routeless), assertion);
        var mgmt = await SendAsync(client, MgmtPath(estate.TemplateId), headlessToken, assertion);

        Assert.Equal(HttpStatusCode.Unauthorized, app.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mgmt.StatusCode);
        // The same uniform message, not a distinct "you have no domains" answer.
        Assert.Contains("Missing or invalid user assertion.", await BodyAsync(app), StringComparison.Ordinal);
        Assert.Equal(await BodyAsync(app), await BodyAsync(mgmt));
    }

    // ── GET /api/mgmt/templates/{id}/tenants/accessible ─────────────────────────

    [Fact]
    public async Task Mgmt_ListsTheAccessibleTenants_WithoutACurrentFlag() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        using var client = factory.CreateApiClient();

        var response = await SendAsync(client, MgmtPath(estate.TemplateId), estate.ConsoleToken,
            await AssertionAsync(factory, estate.UserId, ConsoleDomain));
        var raw = await BodyAsync(response);
        var tenants = JsonDocument.Parse(raw).RootElement.GetProperty("tenants").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Same slug ordering as the App API's answer, and likewise not the insertion order.
        Assert.Equal(["abacus", "acme", "globex", "vip"],
            tenants.Select(t => t.GetProperty("slug").GetString()));
        Assert.Equal("vip.example.com", tenants[3].GetProperty("domain").GetString());
        // A management stack is not one of the tenants it lists, so the field is absent rather than false.
        Assert.All(tenants, t => Assert.False(t.TryGetProperty("current", out _)));
        Assert.DoesNotContain("stackId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// The grant gate keeps its place at the front of the chain: an ungranted template answers the
    /// surface's uniform 404 before the assertion is looked at, so this endpoint cannot become the oracle
    /// for template ids the rest of the management API refuses to be.
    /// </summary>
    [Fact]
    public async Task Mgmt_AnUngrantedTemplate_Is404_AheadOfAnyAssertionCheck() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        var ungranted = await factory.AddTemplateAsync("unrelated", "{tenant}.unrelated.example.com");
        using var client = factory.CreateApiClient();
        var assertion = await AssertionAsync(factory, estate.UserId, ConsoleDomain);

        var existing = await SendAsync(client, MgmtPath(ungranted), estate.ConsoleToken, assertion);
        var missing = await SendAsync(client, MgmtPath(424242), estate.ConsoleToken, assertion);
        // Same answer with no assertion at all: the 404 does not depend on getting that part right.
        var anonymousUser = await SendAsync(client, MgmtPath(ungranted), estate.ConsoleToken, assertion: null);

        Assert.Equal(HttpStatusCode.NotFound, existing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, anonymousUser.StatusCode);
        Assert.Equal(await BodyAsync(existing), await BodyAsync(missing));
        Assert.Equal(await BodyAsync(existing), await BodyAsync(anonymousUser));
        Assert.Contains("Template not found.", await BodyAsync(existing), StringComparison.Ordinal);
    }

    /// <summary>
    /// The audience must be one of the <em>management</em> stack's own domains. An assertion minted for one
    /// of the tenants — which a management UI could plausibly get hold of — proves nothing about who is
    /// standing in front of the console, and is refused.
    /// </summary>
    [Fact]
    public async Task Mgmt_WithAnAssertionMintedForATenantRatherThanTheConsole_Is401() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        using var client = factory.CreateApiClient();

        var response = await SendAsync(client, MgmtPath(estate.TemplateId), estate.ConsoleToken,
            await AssertionAsync(factory, estate.UserId, CallerDomain));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Missing or invalid user assertion.", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mgmt_WithoutAnAssertion_Is401() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        using var client = factory.CreateApiClient();

        var response = await SendAsync(client, MgmtPath(estate.TemplateId), estate.ConsoleToken, assertion: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Missing or invalid user assertion.", await BodyAsync(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// The literal <c>accessible</c> segment outranks <c>{slug}</c> in routing, so the two endpoints do not
    /// collide — the unfiltered tenant listing is untouched and still answers everything.
    /// </summary>
    [Fact]
    public async Task Mgmt_TheUnfilteredListingIsUnchanged_AndStillShowsWhatTheUserCannotEnter() {
        using var factory = AuthOn();
        var estate = await SeedEstateAsync(factory);
        using var client = factory.CreateApiClient();

        var response = await SendAsync(
            client, $"/api/mgmt/templates/{estate.TemplateId}/tenants", estate.ConsoleToken, assertion: null);
        var tenants = JsonDocument.Parse(await BodyAsync(response))
            .RootElement.GetProperty("tenants").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("secret", tenants.Select(t => t.GetProperty("slug").GetString()));
    }

    // ── Auth off ────────────────────────────────────────────────────────────────

    /// <summary>
    /// With central auth switched off no assertion can exist, so asking for one would be nonsense: both
    /// endpoints are 404, exactly as the forward-auth surface is. Mapped rather than left out, or the SPA
    /// fallback would answer index.html with a 200.
    /// </summary>
    [Fact]
    public async Task WithAuthDisabled_BothEndpointsAre404_WhileTheRestOfTheSurfaceStillAnswers() {
        using var factory = new WatchtowerApiFactory();
        var estate = await SeedEstateAsync(factory);
        using var client = factory.CreateApiClient();

        var app = await SendAsync(client, AppPath, estate.CallerToken, assertion: null);
        var mgmt = await SendAsync(client, MgmtPath(estate.TemplateId), estate.ConsoleToken, assertion: null);
        var unfiltered = await SendAsync(
            client, $"/api/mgmt/templates/{estate.TemplateId}/tenants", estate.ConsoleToken, assertion: null);

        Assert.Equal(HttpStatusCode.NotFound, app.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, mgmt.StatusCode);
        // Not a blanket closure of the surface — everything that does not need an identity still works.
        Assert.Equal(HttpStatusCode.OK, unfiltered.StatusCode);
    }

    // ── Estate ──────────────────────────────────────────────────────────────────

    /// <summary>The seeded estate: one template, its tenants in every access mode, and the two callers.</summary>
    /// <param name="TemplateId">The template both endpoints are asked about.</param>
    /// <param name="CallerToken">App API token of the <c>acme</c> tenant, which calls the App API endpoint.</param>
    /// <param name="ConsoleToken">App API token of the granted management stack.</param>
    /// <param name="UserId">The visiting account every assertion is minted for.</param>
    private sealed record Estate(int TemplateId, string CallerToken, string ConsoleToken, int UserId);

    /// <summary>
    /// One template with a tenant per interesting access mode, a second template that must never show up,
    /// the calling tenant, and a management stack granted the first template.
    /// </summary>
    private static async Task<Estate> SeedEstateAsync(WatchtowerApiFactory factory) {
        var billing = await factory.AddTemplateAsync("billing");
        var portal = await factory.AddTemplateAsync("portal", "{tenant}.portal.example.com");
        var userId = await factory.AddUserAsync("alice", "alice@example.invalid");

        // The caller itself: a Public tenant, so it appears in its own answer with current = true.
        var caller = await factory.AddTenantAsync(billing, "acme", domain: CallerDomain);
        await factory.AddTenantAsync(billing, "globex", accessMode: AccessMode.Authenticated);
        // Restricted, and this user holds the grant.
        var vip = await factory.AddTenantAsync(billing, "vip", accessMode: AccessMode.Restricted);
        await factory.GrantAsync(await factory.PrimaryRouteIdAsync(vip), userId);
        // Restricted with no grant for this user.
        await factory.AddTenantAsync(billing, "secret", accessMode: AccessMode.Restricted);
        // A mode written by a newer build: unreadable here, and therefore not accessible.
        var future = await factory.AddTenantAsync(billing, "future", accessMode: AccessMode.Restricted);
        await factory.SetRawAccessModeAsync(await factory.PrimaryRouteIdAsync(future), "99");
        await factory.GrantAsync(await factory.PrimaryRouteIdAsync(future), userId);
        // Provisioned but routeless: there is no domain to switch to.
        await factory.AddTenantAsync(billing, "roadless", withRoute: false);
        // Another template's tenant, which this template's visitor must never be offered.
        await factory.AddTenantAsync(portal, "elsewhere", "portal", "elsewhere.portal.example.com");
        // Seeded last and sorts first ("ab" < "ac"), so insertion order and slug order disagree and the
        // "by slug ascending" assertions have something to bite on.
        //
        // With one honest limit worth knowing before trusting them: deleting the sort in
        // TenantDiscoveryService alone does *not* fail them, because SQLite answers the tenants query from
        // the unique (template_id, tenant_slug) index and hands back slug order anyway. What this fixture
        // does catch is the realistic drift — the tenants query acquiring an order of its own, e.g. the
        // `OrderByDescending(s => s.Id)` ("newest first") that the neighbouring ListTenantsAsync uses.
        // Verified by mutation: that change without the sort fails both listing tests, and with the sort
        // keeps them green. So the sort is the guarantee, and this is the assertion that holds it.
        await factory.AddTenantAsync(billing, "abacus", accessMode: AccessMode.Authenticated);

        var (consoleId, consoleToken) = await factory.AddCallerStackAsync("vendor-console", domain: ConsoleDomain);
        await factory.GrantManagementAsync(consoleId, billing);

        return new Estate(billing, await factory.AppApiTokenAsync(caller), consoleToken, userId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string MgmtPath(int templateId) => $"/api/mgmt/templates/{templateId}/tenants/accessible";

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, string url, string? token, string? assertion) {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) request.Headers.Add("Authorization", $"Bearer {token}");
        if (assertion is not null) request.Headers.TryAddWithoutValidation(RouteAccessPolicy.JwtHeaderName, assertion);
        return client.SendAsync(request, Ct);
    }

    /// <summary>Mints the assertion a stack serving <paramref name="audience"/> would have been forwarded.</summary>
    private static async Task<string> AssertionAsync(
        WatchtowerApiFactory factory, int userId, string audience) {
        var token = string.Empty;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var signer = sp.GetRequiredService<AuthTokenSigner>();
            var user = await db.Users.SingleAsync(u => u.Id == userId, Ct);
            // For the account's own realm, which is what verify would have minted it for.
            var realm = await db.Realms.SingleAsync(r => r.Id == user.RealmId, Ct);
            var realms = sp.GetRequiredService<RealmResolver>();
            token = signer.Mint(user, audience, await realms.IdentityForAsync(realm, Ct));
        });
        return token;
    }

    private static Task<string> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(Ct);
}
