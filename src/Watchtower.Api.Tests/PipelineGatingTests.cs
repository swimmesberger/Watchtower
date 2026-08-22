using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Covers what turning <c>Auth:Enabled</c> on and off does to the surfaces that are not JSON-RPC handlers:
/// the login routes, the SSE streams, the always-open endpoints, and the session bootstrap the login page
/// itself depends on. This is the blast radius <c>[assembly: ElarionAuthorizationDefaults]</c> created
/// (docs/central-auth/design.md §11), so it is the part worth pinning down.
/// </summary>
public sealed class PipelineGatingTests {
    private const string DeployStream = "/api/stacks/events/1/stream";
    private const string ContainerLogStream = "/api/containers/abc/logs";
    private const string MgmtTemplates = "/api/mgmt/templates";

    // Reads from the database and the in-process broadcaster only — no Docker, so it is the stream that
    // can be driven to completion in a test. The container-log stream is only asserted on its 401 path,
    // which short-circuits before the Docker client is touched.
    private static WatchtowerApiFactory AuthEnabled() => new(("Watchtower:Auth:Enabled", "true"));

    private static WatchtowerApiFactory AuthDisabled() => new();

    [Fact]
    public async Task AuthDisabled_LeavesEveryExistingSurfaceOpen() {
        using var factory = AuthDisabled();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        // The login routes answer 404 rather than 405-from-the-SPA-fallback: there is no user database in
        // play, so a login form would be a dead end.
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "admin", password = "whatever" }, ct);
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);

        var logout = await client.PostAsync("/api/auth/logout", content: null, ct);
        Assert.Equal(HttpStatusCode.NotFound, logout.StatusCode);

        // The SSE stream and the RPC surface behave exactly as they did before authorization existed.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(DeployStream, ct)).StatusCode);
        Assert.Contains("\"result\"", await RpcAsync(client, "credentials.list", cookie: null), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", ct)).StatusCode);
    }

    [Fact]
    public async Task AuthDisabled_SessionSnapshotReportsTheImplicitLocalAdministrator() {
        using var factory = AuthDisabled();
        using var client = factory.CreateApiClient();

        var body = await RpcAsync(client, "elarion.session", cookie: null);

        Assert.Contains("\"isAuthenticated\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"local\"", body, StringComparison.Ordinal);
        Assert.Contains("\"Admin\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthDisabled_TheAdminOnlyUsersModuleIsReachableAndAdvertised() {
        using var factory = AuthDisabled();
        using var client = factory.CreateApiClient();

        // users.* is [RequireRole("Admin")]; with authentication off the caller is
        // ImplicitAdminCurrentUser, which holds that role — so the module must work, not 403.
        var list = await RpcAsync(client, "users.list", cookie: null);
        Assert.Contains("\"users\":[]", list, StringComparison.Ordinal);

        // The SPA's users module is gated on `when: { module: 'Users' }`, which reads this snapshot.
        var session = await RpcAsync(client, "elarion.session", cookie: null);
        Assert.Contains("\"Users\":true", session, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthEnabled_ClosesTheStreamsAndTheRpcSurfaceToAnonymousCallers() {
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(DeployStream, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ContainerLogStream, ct)).StatusCode);
        Assert.Contains("-32005", await RpcAsync(client, "credentials.list", cookie: null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthEnabled_KeepsTheDeliberatelyOpenEndpointsOpen() {
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        // /health is a liveness probe with no data behind it.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", ct)).StatusCode);

        // Caddy's on-demand-TLS gate: consulted before any user exists in the request. 400 (not 401) — it
        // is answering, just without a domain. Who may ask is a separate gate (see ProxyAskTests). Its own
        // host, with the caddy provider named: the endpoint is mapped only under the provider that asks,
        // and the default is the in-process one, which holds the route table in memory (ADR-0017).
        using var caddyFactory = new WatchtowerApiFactory(
            ("Watchtower:Auth:Enabled", "true"), ("Watchtower:Proxy:Provider", "caddy"));
        using var caddyClient = caddyFactory.CreateApiClient();
        Assert.Equal(
            HttpStatusCode.BadRequest, (await caddyClient.GetAsync("/api/proxy/ask", ct)).StatusCode);

        // The deploy webhook carries its own per-stack bearer token; a CI runner has no browser session.
        // 404 because no such stack exists — the point is that it is not a 401 from the session gate.
        var webhook = await client.PostAsync("/api/webhooks/stacks/1/deploy", content: null, ct);
        Assert.Equal(HttpStatusCode.NotFound, webhook.StatusCode);
    }

    /// <summary>
    /// The two public token-authenticated surfaces stay out of the session gate: their callers are
    /// deployed applications and a vendor's management UI, neither of which has a browser session. They
    /// must answer with <em>their own</em> 401 — the one that says the bearer token was missing or
    /// unknown — rather than the session middleware's, or the token would never get a chance to be the
    /// gate it is supposed to be.
    /// </summary>
    [Fact]
    public async Task AuthEnabled_LeavesTheTokenAuthenticatedPublicSurfacesToTheirOwnGate() {
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        foreach (var url in new[] { "/api/app/self", MgmtTemplates, $"{MgmtTemplates}/1/tenants" }) {
            var anonymous = await client.GetAsync(url, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            // The body is the surface's own, which is what distinguishes it from the session gate's
            // empty 401 (the session challenge also sets WWW-Authenticate; this never does).
            Assert.Contains("Missing or invalid App API token.",
                await anonymous.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            Assert.Empty(anonymous.Headers.WwwAuthenticate);
        }
    }

    /// <summary>
    /// The grant handlers behind that surface are the opposite case: they are operator-only privilege
    /// management, so with authentication on they are closed to an anonymous JSON-RPC caller like every
    /// other handler.
    /// </summary>
    [Fact]
    public async Task AuthEnabled_ClosesTheGrantManagementRpcMethods() {
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();

        foreach (var method in new[] {
                     "templates.listGrants", "templates.grantManagement", "templates.revokeManagement",
                 })
            Assert.Contains("-32005", await RpcAsync(client, method, cookie: null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthEnabled_SessionSnapshotStaysAnonymouslyReadable() {
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();

        // The SPA calls this before it can possibly have a session — it is how the login page learns it
        // needs to show itself. It must answer, and it must answer "not authenticated" rather than fail.
        var body = await RpcAsync(client, "elarion.session", cookie: null);

        Assert.Contains("\"isAuthenticated\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthEnabled_ASignedInCallerReachesTheStreams() {
        using var factory = AuthEnabled();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { userName = "admin", password = WatchtowerApiFactory.AdminPassword },
            ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

        // EventSource sends cookies on same-origin requests, which is what keeps the UI working.
        var request = new HttpRequestMessage(HttpMethod.Get, DeployStream);
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, ct)).StatusCode);
    }

    private static async Task<string> RpcAsync(HttpClient client, string method, string? cookie) {
        var request = new HttpRequestMessage(HttpMethod.Post, "/rpc") {
            Content = JsonContent.Create(new { jsonrpc = "2.0", method, @params = new { }, id = "1" }),
        };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
