using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The forward-auth decision on its own (docs/central-auth/design.md §5), asked the way an in-process proxy
/// asks it: an <see cref="AccessRequest"/> in, an <see cref="AccessDecision"/> out, with no HTTP hop and no
/// status code in sight.
/// </summary>
/// <remarks>
/// The counterpart of <c>Watchtower.Api.Tests.AccessVerifyTests</c>, which drives the same logic through the
/// endpoint the way Caddy does. Both exist on purpose: the endpoint tests pin the wire contract (statuses,
/// headers, the denial page), and these pin the decision itself, so a second transport can be added without
/// the verdicts being re-derived anywhere.
/// </remarks>
public sealed class AccessVerifierTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AppDomain = "app.example.invalid";
    private const string Client = "from 203.0.113.7";

    private static (string Key, string? Value)[] AuthOn() =>
        [("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost)];

    [Fact]
    public async Task UnknownHost_IsNotAWatchtowerApp() {
        using var host = AuthTestHost.Start(AuthOn());

        var decision = await DecideAsync(host, Request("nobody.example.invalid"));

        Assert.IsType<AccessDecision.NotFound>(decision);
    }

    [Fact]
    public async Task PublicRoute_Passes() {
        using var host = AuthTestHost.Start(AuthOn());
        await host.AddRouteAsync(AppDomain, AccessMode.Public);

        var decision = await DecideAsync(host, Request(AppDomain));

        // Pass carries no headers at all, which is the whole statement: nothing identifies this request.
        Assert.IsType<AccessDecision.Pass>(decision);
    }

    [Fact]
    public async Task ExemptPath_Passes_WithoutIdentity_EvenForASignedInVisitor() {
        using var host = AuthTestHost.Start(AuthOn());
        var route = await host.AddRouteAsync(
            AppDomain, AccessMode.Authenticated, bypassPaths: "/webhooks/",
            identityHeaderMode: IdentityHeaderMode.Remote);
        var userId = await host.AddUserAsync("alice");
        var cookie = await AppSessionAsync(host, userId, route.Id);

        var decision = await DecideAsync(host, Request(AppDomain, "/webhooks/github", cookie: cookie));

        // A bypass is "no access control on this path", not "anonymous access as somebody" — the session is
        // never consulted, so the answer is the identity-free Pass rather than an Allow.
        Assert.IsType<AccessDecision.Pass>(decision);
    }

    [Fact]
    public async Task AnonymousNonNavigation_IsUnauthorized() {
        using var host = AuthTestHost.Start(AuthOn());
        await host.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        // An XHR wants a status it can branch on, not a login page it cannot render.
        var decision = await DecideAsync(host, Request(AppDomain, "/api/items"));

        Assert.IsType<AccessDecision.Unauthorized>(decision);
    }

    [Fact]
    public async Task AnonymousNavigation_IsSentToTheRealmsLogin() {
        using var host = AuthTestHost.Start(AuthOn());
        await host.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        var decision = await DecideAsync(
            host, Request(AppDomain, "/reports?range=30d", isBrowserNavigation: true));

        // Assembled from stored values only: literal https, the realm's login host, and the route's own
        // domain — the caller supplies nothing but the (bounded, rooted, encoded) path.
        var redirect = Assert.IsType<AccessDecision.RedirectToLogin>(decision);
        Assert.Equal(
            $"https://{AuthHost}/login?redirect_uri=" +
            Uri.EscapeDataString($"https://{AppDomain}/reports?range=30d"),
            redirect.Url);
    }

    [Fact]
    public async Task WithoutALoginHost_EvenANavigationIsUnauthorized() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Enabled", "true"));
        await host.AddRouteAsync(AppDomain, AccessMode.Authenticated);

        // There is nowhere to send them, and guessing a login host would be inventing a redirect target out
        // of request data — which is exactly what the open-redirect guard exists to prevent.
        var decision = await DecideAsync(host, Request(AppDomain, "/reports", isBrowserNavigation: true));

        Assert.IsType<AccessDecision.Unauthorized>(decision);
    }

    [Fact]
    public async Task AuthorisedAccount_IsAllowed_WithTheAssertionAndTheModesHeaders() {
        using var host = AuthTestHost.Start(AuthOn());
        var route = await host.AddRouteAsync(
            AppDomain, AccessMode.Restricted, identityHeaderMode: IdentityHeaderMode.Remote);
        var userId = await host.AddUserAsync("alice");
        await host.GrantUserAsync(route.Id, userId);
        var cookie = await AppSessionAsync(host, userId, route.Id);

        var decision = await DecideAsync(host, Request(AppDomain, "/", cookie: cookie));

        var allow = Assert.IsType<AccessDecision.Allow>(decision);
        // The signed assertion always comes first — it is the source of truth, not a mode-gated convenience.
        Assert.Equal(RouteAccessPolicy.JwtHeaderName, allow.Headers[0].Key);
        Assert.NotEmpty(allow.Headers[0].Value);
        Assert.Equal("alice", HeaderValue(allow, IdentityForwarding.RemoteUser));
    }

    /// <summary>
    /// Cloudflare mode carries the <em>same</em> Watchtower-signed assertion under Cloudflare's header name,
    /// which is what lets an app written against <c>Cf-Access-Jwt-Assertion</c> keep working with only its
    /// JWKS and issuer configuration re-pointed here — two different tokens would fail that promise.
    /// </summary>
    [Fact]
    public async Task CloudflareMode_DuplicatesTheAssertionUnderTheCloudflareHeaderName() {
        using var host = AuthTestHost.Start(AuthOn());
        var route = await host.AddRouteAsync(
            AppDomain, AccessMode.Authenticated, identityHeaderMode: IdentityHeaderMode.Cloudflare);
        var userId = await host.AddUserAsync("alice");
        var cookie = await AppSessionAsync(host, userId, route.Id);

        var decision = await DecideAsync(host, Request(AppDomain, "/", cookie: cookie));

        var allow = Assert.IsType<AccessDecision.Allow>(decision);
        var assertion = HeaderValue(allow, RouteAccessPolicy.JwtHeaderName);
        Assert.NotEmpty(assertion);
        Assert.Equal(assertion, HeaderValue(allow, IdentityForwarding.CfAccessJwtAssertion));
    }

    [Fact]
    public async Task SignedInButNotGranted_IsDenied_AndAudited() {
        using var host = AuthTestHost.Start(AuthOn());
        var route = await host.AddRouteAsync(AppDomain, AccessMode.Restricted);
        var userId = await host.AddUserAsync("alice");
        var cookie = await AppSessionAsync(host, userId, route.Id);

        var decision = await DecideAsync(host, Request(AppDomain, "/", cookie: cookie));

        // Not a redirect: they have already signed in, so sending them back to the login page would loop.
        var denied = Assert.IsType<AccessDecision.Denied>(decision);
        Assert.Equal("Access denied", denied.Title);
        // Plain text, not HTML — escaping belongs to whoever renders a page from it.
        Assert.Contains(AppDomain, denied.Message, StringComparison.Ordinal);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == AuthEventKinds.AccessDenied, TestContext.Current.CancellationToken);
        Assert.False(row.Success);
        Assert.Equal(AppDomain, row.Target);
        Assert.Equal(Client, row.Detail);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>One request as the verifier sees it, whichever transport it arrived on.</summary>
    private static AccessRequest Request(
        string host, string uri = "/", string? cookie = null, bool isBrowserNavigation = false) =>
        new(host, uri, cookie, isBrowserNavigation, Client);

    private static async Task<AccessDecision> DecideAsync(AuthTestHost host, AccessRequest request) {
        await using var scope = host.Services.CreateAsyncScope();
        var verifier = scope.ServiceProvider.GetRequiredService<AccessVerifier>();
        return await verifier.DecideAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Mints the <c>__wt_access</c> token a signed-in visitor of one app would be carrying.</summary>
    private static async Task<string> AppSessionAsync(AuthTestHost host, int userId, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var ct = TestContext.Current.CancellationToken;
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        return await sessions.CreateAppSessionAsync(user, routeId, ct);
    }

    private static string HeaderValue(AccessDecision.Allow allow, string name) =>
        Assert.Single(allow.Headers, h => h.Key == name).Value;
}
