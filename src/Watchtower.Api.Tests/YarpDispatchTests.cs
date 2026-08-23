using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Api.Proxy;
using Yarp.ReverseProxy.Forwarder;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Host dispatch for the in-process proxy (ADR-0022): which requests leave for a container,
/// which ones stay with Watchtower, and what the upstream is told about the one that left.
/// </summary>
/// <remarks>
/// Every test drives the real <c>Program.cs</c> pipeline, because the property under test is a pipeline
/// property — the dispatcher runs ahead of routing, static files, the SPA fallback and
/// <c>UseForwardedHeaders</c>, and any of those winning first would be the bug. What is substituted is only
/// the far end: a recording <c>IHttpForwarder</c> that runs the real transformer and answers with a marker
/// body instead of dialling a container alias that does not resolve here.
/// </remarks>
public sealed class YarpDispatchTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AppDomain = "app.example.invalid";

    /// <summary>The alias <c>AddRouteAsync</c>'s stack produces: <c>{project}-{service}</c> on port 8080.</summary>
    private const string AppUpstream = "http://app-web:8080";

    [Fact]
    public async Task RequestForARouteHost_IsForwardedToTheStackAlias() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AppDomain}/reports?range=30d", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RecordingHttpForwarder.MarkerBody, await Body(response));

        var forwarded = factory.Forwarder.Single();
        Assert.Equal(AppUpstream, forwarded.DestinationPrefix);
        // Path and query travel unchanged; only the authority is rewritten.
        Assert.Equal($"{AppUpstream}/reports?range=30d", forwarded.RequestUri?.ToString());
    }

    /// <summary>
    /// The header YARP's default transformer drops. An application behind the proxy builds its absolute
    /// URLs, cookie domains and redirects from it, so seeing <c>app-web:8080</c> there would break every one
    /// of them — and a virtual-hosting upstream would not even find the right site.
    /// </summary>
    [Fact]
    public async Task TheUpstreamSeesTheOriginalHost() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        await client.GetAsync($"https://{AppDomain}/", Ct);

        var forwarded = factory.Forwarder.Single();
        Assert.Equal(AppDomain, forwarded.Host);
        Assert.Equal(AppDomain, forwarded.Header("X-Forwarded-Host"));
        Assert.Equal("https", forwarded.Header("X-Forwarded-Proto"));
    }

    /// <summary>
    /// Watchtower is the first hop, so the transport headers state what the <em>connection</em> says and
    /// nothing else. Appending to a client-supplied value would let any visitor prepend a hop of their
    /// choosing and have the upstream read it as the origin.
    /// </summary>
    [Fact]
    public async Task ForwardedTransportHeaders_AreSetNotAppended() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{AppDomain}/");
        request.Headers.Add("X-Forwarded-For", "1.2.3.4");
        request.Headers.Add("X-Forwarded-Host", "evil.example.invalid");
        request.Headers.Add("X-Forwarded-Proto", "http");
        await client.SendAsync(request, Ct);

        var forwarded = factory.Forwarder.Single();
        Assert.Equal(AppDomain, forwarded.Header("X-Forwarded-Host"));
        Assert.Equal("https", forwarded.Header("X-Forwarded-Proto"));
        // Either the real remote address or nothing at all — never the client's claim, and never the
        // client's claim with a hop appended to it.
        var forwardedFor = forwarded.Header("X-Forwarded-For");
        Assert.DoesNotContain("1.2.3.4", forwardedFor ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>app.example.invalid.</c> is the same host, fully qualified — a shape a browser or a resolver-aware
    /// client can genuinely produce. Missing the table on it would not be a 404: the request would fall
    /// through to Watchtower's own pipeline <em>on the tenant's domain</em>, serving them the management SPA
    /// where their application should be, with no access check anywhere in sight.
    /// </summary>
    [Fact]
    public async Task TrailingDotHost_IsStillDispatched() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AppDomain}./reports", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RecordingHttpForwarder.MarkerBody, await Body(response));
        var forwarded = factory.Forwarder.Single();
        // And the upstream is told the canonical name, not the one with the dot on the end: the host the
        // route table matched is the host the application is told it is answering as.
        Assert.Equal(AppDomain, forwarded.Host);
        Assert.Equal(AppDomain, forwarded.Header("X-Forwarded-Host"));
    }

    /// <summary>
    /// The forwarder names the failures it can diagnose — a timed-out upstream is a 504, not a bare 502 —
    /// and the dispatcher's fallback must not overwrite that. It steps in only over an untouched 200.
    /// </summary>
    [Fact]
    public async Task AForwarderStatus_IsNotFlattenedInto502() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();
        factory.Forwarder.Error = ForwarderError.RequestTimedOut;
        factory.Forwarder.FailureStatusCode = StatusCodes.Status504GatewayTimeout;

        var response = await client.GetAsync($"https://{AppDomain}/", Ct);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    /// <summary>A failure that leaves the response untouched still gets the honest default.</summary>
    [Fact]
    public async Task AFailureWithNoStatusOfItsOwn_Becomes502() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();
        factory.Forwarder.Error = ForwarderError.RequestCreation;

        var response = await client.GetAsync($"https://{AppDomain}/", Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task LoginHost_OverHttps_ReachesWatchtower() {
        using var factory = LoginHostEstate();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        // The login host is a Watchtower route in the table (ADR-0023 — the configured Auth:Host was
        // converted into one at startup) and so is marked Local: Watchtower serves it itself, and
        // forwarding it would be forwarding to ourselves.
        var response = await client.GetAsync($"https://{AuthHost}/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await Body(response), StringComparison.Ordinal);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// Serving itself is not the same as serving itself over anything. A Watchtower route gets the same
    /// upgrade a forwarded one does — it is an ordinary TLS site — because this is where the central
    /// session cookie is set, and a page reached over plain HTTP would set it without its Secure
    /// attribute.
    /// </summary>
    [Fact]
    public async Task LoginHost_OverPlainHttp_Redirects() {
        using var factory = LoginHostEstate();
        using var client = factory.CreateApiClient();
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"http://{AuthHost}/login?redirect_uri=x", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal($"https://{AuthHost}/login?redirect_uri=x", response.Headers.Location?.ToString());
    }

    /// <summary>
    /// A Watchtower route created outright rather than converted from configuration, and in a customer
    /// realm rather than the operator one — the ordinary way one comes into existence (ADR-0023). It is
    /// served in process just the same: which realm's login page a hostname carries is an auth question,
    /// and "who serves this hostname" is not.
    /// </summary>
    [Fact]
    public async Task AWatchtowerRouteCreatedOutright_ReachesWatchtower() {
        using var factory = WatchtowerApiFactory.WithYarpProxy(("Watchtower:Auth:Enabled", "true"));
        using var client = factory.CreateApiClient();
        var acme = await factory.AddRealmAsync("acme");
        await factory.AddWatchtowerRouteAsync("login.acme.invalid", acme, makeLoginRoute: true);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync("https://login.acme.invalid/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await Body(response), StringComparison.Ordinal);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    private static WatchtowerApiFactory LoginHostEstate() => WatchtowerApiFactory.WithYarpProxy(
        ("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost));

    [Fact]
    public async Task RequestForAnUnknownHost_ReachesWatchtower() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        // Nobody routed it — the published management port, or a stray domain pointed at this host.
        var response = await client.GetAsync("https://nobody.example.invalid/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    [Fact]
    public async Task TlsRoute_OverPlainHttp_Redirects() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public, tlsEnabled: true);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"http://{AppDomain}/path?q=1", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal($"https://{AppDomain}/path?q=1", response.Headers.Location?.ToString());
        // Nothing reaches the upstream over a scheme the operator asked to have upgraded.
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    [Fact]
    public async Task NonTlsRoute_IsNotRedirected() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public, tlsEnabled: false);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"http://{AppDomain}/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http", factory.Forwarder.Single().Header("X-Forwarded-Proto"));
    }

    /// <summary>
    /// The escape hatch for a deployment fronted by another TLS terminator, where redirecting again would
    /// loop between the two.
    /// </summary>
    [Fact]
    public async Task RedirectCanBeDisabled() {
        using var factory = WatchtowerApiFactory.WithYarpProxy(
            ("Watchtower:Proxy:Yarp:RedirectHttpToHttps", "false"));
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public, tlsEnabled: true);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"http://{AppDomain}/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// Kestrel's 30 MB body cap is a limit on requests to <em>Watchtower</em>. Leaving it in force on a
    /// proxied request would make an application behind the proxy lose uploads it handles fine on its own.
    /// </summary>
    /// <remarks>
    /// Driven against the middleware directly rather than through the test host, because the feature is the
    /// thing under test and <c>TestServer</c> does not install a body-size limit at all — a run through the
    /// pipeline would assert "no limit" against a context that never had one, which is not a test.
    /// </remarks>
    [Fact]
    public async Task MaxRequestBodySize_IsLifted() {
        var forwarder = new RecordingHttpForwarder();
        var table = new ProxyRouteTable();
        table.Replace(ProxyRouteTable.From([
            new ProxySite(AppDomain, "app-web", 8080, Tls: false),
        ]));
        using var client = new ProxyForwardHttpClient();
        var middleware = new YarpHostDispatchMiddleware(
            _ => Task.FromException(new InvalidOperationException("The request must not fall through.")),
            table, forwarder, client,
            new YarpListenerState(),
            new StaticOptionsMonitor(new WatchtowerOptions()),
            NullLogger<YarpHostDispatchMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(AppDomain);
        context.Request.Path = "/upload";
        var size = new StubMaxRequestBodySizeFeature { MaxRequestBodySize = 30 * 1024 * 1024 };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(size);

        await middleware.InvokeAsync(context);

        Assert.Null(size.MaxRequestBodySize);
        Assert.Null(forwarder.Single().MaxRequestBodySize);
    }

    /// <summary>
    /// With another provider selected the route table is empty, so the dispatcher costs one failed lookup
    /// and the request is the ordinary Watchtower request it always was. The middleware is in the pipeline
    /// regardless because the provider is switchable from Settings without a restart.
    /// </summary>
    [Fact]
    public async Task WhenTheProviderIsInactive_NothingIsDispatched() {
        using var factory = new WatchtowerApiFactory(
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", "caddy"));
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AppDomain}/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await Body(response), StringComparison.Ordinal);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    // ── Ingress versus the management plane ───────────────────────────────────

    /// <summary>
    /// The invariant this whole split exists for. Under Caddy an unknown host on 80/443 got nothing at all,
    /// because there was no site block for it; sharing one Kestrel endpoint between ingress and the
    /// management plane quietly replaced that with "serve the management SPA to whoever asks" —
    /// <c>http://&lt;public-ip&gt;/</c> reaching the login page with authentication on, and the entire UI
    /// with it off.
    /// </summary>
    [Theory]
    [InlineData(8081)]
    [InlineData(8443)]
    public async Task UnknownHost_OnAnIngressPort_Is404(int port) {
        using var factory = WatchtowerApiFactory.WithIngress();
        using var client = factory.CreateApiClient(port);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync("https://nobody.example.invalid/health", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Nothing about what else lives here — not a body, not a redirect to a login host.
        Assert.Equal("", await Body(response));
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// The other half of the rule, and the one that has to keep working: on ingress a routed host is
    /// forwarded exactly as before. Without this the management-port refusal below could be inverted — or
    /// the whole port test made to fail closed — and the suite would still be green.
    /// </summary>
    [Fact]
    public async Task RouteHost_OnAnIngressPort_IsForwarded() {
        using var factory = WatchtowerApiFactory.WithIngress();
        using var client = factory.CreateApiClient(8443);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AppDomain}/reports?range=30d", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RecordingHttpForwarder.MarkerBody, await Body(response));
        Assert.Equal($"{AppUpstream}/reports?range=30d", factory.Forwarder.Single().RequestUri?.ToString());
    }

    /// <summary>
    /// The reserved prefix is Watchtower answering on the <em>tenant's</em> domain — a routed host, on
    /// ingress, that must not be refused. It is the one place where "this host is a route" and "this
    /// request stays here" are both true, so a port rule written a shade too broadly breaks every
    /// cross-domain sign-in and nothing else.
    /// </summary>
    [Theory]
    [InlineData(8443)]
    [InlineData(8081)]
    public async Task DotWatchtowerCallback_OnAnIngressRouteHost_ReachesWatchtower(int port) {
        using var factory = IngressLoginHostEstate();
        using var client = factory.CreateApiClient(port);
        await factory.AddRouteAsync(AppDomain, AccessMode.Authenticated, tlsEnabled: false);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"http://{AppDomain}/.watchtower/userinfo", Ct);

        // Whatever the endpoint answers an anonymous caller, it is Watchtower answering — not the
        // dispatcher's 404, and not the upstream.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// The same request on the management endpoint is the ordinary one it always was: this port is
    /// Watchtower's own UI and API, answering for any name, which is exactly why it is the port an operator
    /// binds to a private interface rather than publishing.
    /// </summary>
    [Fact]
    public async Task UnknownHost_OnTheManagementPort_ReachesWatchtower() {
        using var factory = WatchtowerApiFactory.WithIngress();
        using var client = factory.CreateApiClient(WatchtowerApiFactory.ManagementPort);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync("https://nobody.example.invalid/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await Body(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// And the mirror rule: a tenant's domain is not served on the management endpoint either. Half-serving
    /// it there — the access check and the forward, on a port whose whole point is that it is not ingress —
    /// would be a second way into every routed application, on a port nobody published for that.
    /// </summary>
    [Fact]
    public async Task RouteHost_OnTheManagementPort_Is404() {
        using var factory = WatchtowerApiFactory.WithIngress();
        using var client = factory.CreateApiClient(WatchtowerApiFactory.ManagementPort);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AppDomain}/reports", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// A Watchtower route is the exception on both listeners, and deliberately so: through ingress it is a
    /// hostname Watchtower serves itself on, and on the management endpoint it is how an operator who
    /// bound 8080 privately still reaches the UI.
    /// </summary>
    [Fact]
    public async Task LoginHost_OnTheManagementPort_ReachesWatchtower() {
        using var factory = IngressLoginHostEstate();
        using var client = factory.CreateApiClient(WatchtowerApiFactory.ManagementPort);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AuthHost}/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await Body(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginHost_OnAnIngressPort_ReachesWatchtower() {
        using var factory = IngressLoginHostEstate();
        using var client = factory.CreateApiClient(8443);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AuthHost}/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await Body(response), StringComparison.Ordinal);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// The one thing that must answer on ingress whatever the Host says. It runs ahead of the dispatcher, so
    /// the 404 rule never sees it — and if it did, no certificate would ever be issued: HTTP-01 arrives on
    /// port 80 for a domain that, by definition, has no route serving it yet.
    /// </summary>
    [Fact]
    public async Task AcmeChallenge_OnAnIngressPort_StillAnswers() {
        using var factory = WatchtowerApiFactory.WithIngress();
        using var client = factory.CreateApiClient(8081);
        await factory.ApplyProxyAsync();
        var challenges = factory.Services.GetRequiredService<AcmeHttpChallengeStore>();
        using var published = challenges.Publish("token-abc", "token-abc.key-authorization");

        var answered = await client.GetAsync(
            "http://not-a-route.example.invalid/.well-known/acme-challenge/token-abc", Ct);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal("token-abc.key-authorization", await Body(answered));

        // A token nobody issued is still the challenge middleware's 404, not the dispatcher's — the
        // distinction does not show in the status, which is the point: neither one says what is here.
        var unknown = await client.GetAsync(
            "http://not-a-route.example.invalid/.well-known/acme-challenge/never-issued", Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ── A port that is neither management nor configured ingress ──────────────
    //
    // The classification fails closed: with the management port known, everything else is ingress. The
    // case that matters is a listener the configuration no longer names — Kestrel keeps its existing
    // endpoints when a rebind fails, so moving an ingress port onto one something else holds leaves the
    // old port bound and serving under a configuration that has moved on. Read as management, that port
    // would hand Watchtower's own UI to whoever found it.

    /// <summary>
    /// An unrouted host on such a port gets the ingress answer — nothing — rather than the management
    /// plane. This is the whole point of the exclusion rule.
    /// </summary>
    [Fact]
    public async Task UnknownHost_OnAnUnexpectedPort_Is404() {
        using var factory = WatchtowerApiFactory.WithIngress();
        using var client = factory.CreateApiClient(9999);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync("https://nobody.example.invalid/health", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await Body(response));
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>And a routed host on it is forwarded, exactly as it would be on a named ingress port.</summary>
    [Fact]
    public async Task RouteHost_OnAnUnexpectedPort_IsForwarded() {
        using var factory = WatchtowerApiFactory.WithIngress();
        using var client = factory.CreateApiClient(9999);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AppDomain}/reports", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RecordingHttpForwarder.MarkerBody, await Body(response));
    }

    /// <summary>
    /// A Watchtower route stays the exception there too — it is served on any listener, which is what
    /// keeps a login host reachable through a listener the configuration has stopped naming.
    /// </summary>
    [Fact]
    public async Task LoginHost_OnAnUnexpectedPort_ReachesWatchtower() {
        using var factory = IngressLoginHostEstate();
        using var client = factory.CreateApiClient(9999);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AuthHost}/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await Body(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule that keeps development and every single-listener deployment working: with no ingress
    /// configured at all, no port is ingress — not even one that is not the management port. A dev host
    /// that also binds an <c>https://</c> hosting URL must not have that second listener read as public.
    /// </summary>
    [Theory]
    [InlineData(5080, 5443)]
    [InlineData(5080, null)]
    public void WithNoIngressConfigured_NothingIsIngress(int managementPort, int? otherPort) {
        var snapshot = new YarpListenerSnapshot { ManagementPort = managementPort };

        Assert.False(YarpHostDispatchMiddleware.IsIngress(snapshot, managementPort));
        if (otherPort is { } port) Assert.False(YarpHostDispatchMiddleware.IsIngress(snapshot, port));
    }

    /// <summary>
    /// And where the management port was never derived — <c>TestServer</c>, the unit hosts — the rule
    /// falls back to the configured set, which is the behaviour those hosts have always had.
    /// </summary>
    [Fact]
    public void WithNoManagementPort_TheConfiguredSetDecides() {
        var snapshot = new YarpListenerSnapshot { IngressPorts = new HashSet<int> { 8081, 8443 } };

        Assert.True(YarpHostDispatchMiddleware.IsIngress(snapshot, 8081));
        Assert.False(YarpHostDispatchMiddleware.IsIngress(snapshot, 9999));
    }

    private static WatchtowerApiFactory IngressLoginHostEstate() => WatchtowerApiFactory.WithIngress(
        ("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<string> Body(HttpResponseMessage response) => response.Content.ReadAsStringAsync(Ct);

    /// <summary>An <see cref="IOptionsMonitor{T}"/> over one fixed value, for the direct middleware test.</summary>
    private sealed class StaticOptionsMonitor(WatchtowerOptions value) : IOptionsMonitor<WatchtowerOptions> {
        public WatchtowerOptions CurrentValue => value;
        public WatchtowerOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<WatchtowerOptions, string?> listener) => null;
    }

    /// <summary>The body-size feature <c>TestServer</c> does not provide, so the lift has something to act on.</summary>
    private sealed class StubMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
