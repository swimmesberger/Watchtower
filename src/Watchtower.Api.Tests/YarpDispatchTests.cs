using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
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
        table.PublishHostRoutes([new ProxySite(AppDomain, "app-web", 8080, Tls: false)]);
        using var client = new ProxyForwardHttpClient();
        var middleware = new YarpHostDispatchMiddleware(
            _ => Task.FromException(new InvalidOperationException("The request must not fall through.")),
            table, forwarder, client,
            new YarpListenerState(),
            EmptySection(),
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
        // A realistic base64url token: the store refuses anything that is not shaped like one, so that
        // a stranger looping over invented tokens never reaches the database.
        const string token = "dG9rZW4tYWJjLWNoYWxsZW5nZS12YWx1ZQ";
        await using var published = await challenges.PublishAsync(
            token, $"{token}.key-authorization", "not-a-route.example.invalid", ct: Ct);

        var answered = await client.GetAsync(
            $"http://not-a-route.example.invalid/.well-known/acme-challenge/{token}", Ct);

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal($"{token}.key-authorization", await Body(answered));

        // A token nobody issued is still the challenge middleware's 404, not the dispatcher's — the
        // distinction does not show in the status, which is the point: neither one says what is here.
        var unknown = await client.GetAsync(
            "http://not-a-route.example.invalid/.well-known/acme-challenge/bmV2ZXItaXNzdWVkLXRva2Vu", Ct);

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

    // ── Port-bound routes (ADR-0033) ──────────────────────────────────────────

    /// <summary>The alias <see cref="PortRouteEstate"/>'s stack produces, on the container port it names.</summary>
    private const string PortUpstream = "http://media-jellyfin:8096";

    /// <summary>
    /// The whole rule in one test: on a port route's listener the route is decided by the port, and the
    /// <c>Host</c> header — which a client dialling a bare LAN address writes whatever it likes into —
    /// decides nothing. Including when it names a domain that is itself routed here.
    /// </summary>
    [Theory]
    [InlineData("nas.lan")]
    [InlineData("192.168.1.10")]
    [InlineData(AppDomain)]
    public async Task RequestOnAPortRoutesListener_IsForwardedWhateverTheHostSays(string host) {
        using var factory = PortRouteEstate();
        using var client = factory.CreateApiClient(PortRoutePort);
        await factory.AddPortRouteAsync(PortRoutePort, serviceName: "jellyfin", containerPort: 8096);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{host}/web/index.html?q=1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RecordingHttpForwarder.MarkerBody, await Body(response));
        var forwarded = factory.Forwarder.Single();
        Assert.Equal(PortUpstream, forwarded.DestinationPrefix);
        Assert.Equal($"{PortUpstream}/web/index.html?q=1", forwarded.RequestUri?.ToString());
        // Echoed, not rewritten: an application building absolute URLs has to hear back the address the
        // client actually dialled, and the route has no hostname of its own to substitute.
        Assert.Equal(host, forwarded.Header("X-Forwarded-Host"));
        // Always https — the listener terminates TLS and does nothing else.
        Assert.Equal("https", forwarded.Header("X-Forwarded-Proto"));
    }

    /// <summary>
    /// The address of a port route is <c>host:port</c>, and the port is the half Kestrel has already split
    /// off into <c>Host.Port</c> by the time the dispatcher runs. Forwarding the bare name would tell every
    /// upstream that builds absolute URLs out of <c>Host</c> — Jellyfin, Nextcloud, Grafana, Home Assistant
    /// — that it is answering on <c>https://nas.lan/</c>, which is not an address this deployment serves,
    /// so the first redirect a visitor followed would leave the service behind.
    /// </summary>
    /// <remarks>
    /// A bare IP is the second case rather than a nicety: it is the address a LAN client with no resolver
    /// uses, and it is the one the shared leaf's IP SAN exists for.
    /// </remarks>
    [Theory]
    [InlineData("nas.lan")]
    [InlineData("192.168.1.10")]
    public async Task ThePortIsKeptInTheHostAPortRoutesUpstreamIsTold(string host) {
        using var factory = PortRouteEstate();
        using var client = factory.CreateApiClient(PortRoutePort);
        await factory.AddPortRouteAsync(PortRoutePort, serviceName: "jellyfin", containerPort: 8096);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{host}:{PortRoutePort}/web/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = factory.Forwarder.Single();
        // Both, because an upstream reads whichever of the two it was written against, and the two
        // disagreeing is how a redirect lands on the wrong authority.
        Assert.Equal($"{host}:{PortRoutePort}", forwarded.Host);
        Assert.Equal($"{host}:{PortRoutePort}", forwarded.Header("X-Forwarded-Host"));
    }

    /// <summary>
    /// The strip is not one of the things a port route skips. Nothing a client sent under an identity
    /// header's name reaches an upstream, on any listener — and on this one there is no identity to
    /// replace it with, so a smuggled header would be the only thing the application saw.
    /// </summary>
    [Fact]
    public async Task IdentityHeaders_AreStrippedOnAPortRoute_AndNoneAreAdded() {
        using var factory = PortRouteEstate();
        using var client = factory.CreateApiClient(PortRoutePort);
        await factory.AddPortRouteAsync(PortRoutePort, serviceName: "jellyfin", containerPort: 8096);
        await factory.ApplyProxyAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "https://nas.lan/");
        request.Headers.Add("Remote-User", "smuggled");
        request.Headers.Add("X-Auth-Request-User", "smuggled");
        request.Headers.Add("X-Forwarded-Method", "GET");
        await client.SendAsync(request, Ct);

        var forwarded = factory.Forwarder.Single();
        Assert.Null(forwarded.Header("Remote-User"));
        Assert.Null(forwarded.Header("X-Auth-Request-User"));
        Assert.Null(forwarded.Header("X-Forwarded-Method"));
        Assert.Null(forwarded.Header(RouteAccessPolicy.JwtHeaderName));
    }

    /// <summary>
    /// <c>/.watchtower/*</c> is reserved on a domain route because that is where an anonymous visitor is
    /// handed a session. A port route is public by construction, so nothing redirects anyone there — and
    /// holding the prefix would take a path away from an upstream entitled to serve it.
    /// </summary>
    [Fact]
    public async Task TheReservedPrefix_IsForwardedOnAPortRoute() {
        using var factory = PortRouteEstate();
        using var client = factory.CreateApiClient(PortRoutePort);
        await factory.AddPortRouteAsync(PortRoutePort, serviceName: "jellyfin", containerPort: 8096);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync("https://nas.lan/.watchtower/userinfo", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{PortUpstream}/.watchtower/userinfo", factory.Forwarder.Single().RequestUri?.ToString());
    }

    /// <summary>
    /// No HTTPS upgrade either. The redirect is rebuilt from the route's hostname, and there is none —
    /// so a request that somehow arrived over plain HTTP is served rather than sent to an address that
    /// does not exist.
    /// </summary>
    [Fact]
    public async Task APortRoute_NeverRedirects() {
        using var factory = PortRouteEstate();
        using var client = factory.CreateApiClient(PortRoutePort);
        await factory.AddPortRouteAsync(PortRoutePort, serviceName: "jellyfin", containerPort: 8096);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync("http://nas.lan/web/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// The race: the row is deleted and the socket has not been unbound yet. A bare 404, not a fall
    /// through to the host lookup — otherwise the <c>Host</c> header could pick some other route on a
    /// listener whose own route is gone.
    /// </summary>
    [Fact]
    public async Task APortWithNoRouteLeft_Is404_EvenForARoutedHost() {
        using var factory = PortRouteEstate();
        using var client = factory.CreateApiClient(PortRoutePort);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://{AppDomain}/reports", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await Body(response));
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// And the ingress listeners are unaffected: a port route is reached on its own port and nowhere
    /// else, so it cannot be a second door into a service on 443.
    /// </summary>
    [Fact]
    public async Task APortRoute_IsNotReachableOnTheIngressPorts() {
        using var factory = PortRouteEstate();
        using var client = factory.CreateApiClient(8443);
        await factory.AddPortRouteAsync(PortRoutePort, serviceName: "jellyfin", containerPort: 8096);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync("https://nas.lan/", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// A port route whose listen port collides with an ingress port never captures that port. The
    /// projection refuses to bind such a route — two endpoints on one socket — so the listener on 8443 is
    /// the TLS ingress endpoint and nothing else, and the route table still holding the row must not be
    /// able to turn it into a forward.
    /// </summary>
    /// <remarks>
    /// Reachable in production: <c>Yarp:HttpsPort</c> is an environment-pinnable setting, so it can move
    /// onto a port that create-time validation already accepted for a route. Getting this wrong is not a
    /// missing feature — every request arriving on 443 would be forwarded to that one upstream, past the
    /// host lookup, past the ingress/management split and past the access check, handing every visitor's
    /// session cookie to a service that has no business seeing it.
    /// </remarks>
    [Fact]
    public async Task APortRouteCollidingWithAnIngressPort_DoesNotCaptureIt() {
        // The row says 8443; the projection drops that endpoint because the TLS ingress listener has it.
        using var factory = WatchtowerApiFactory.WithIngress(
            ("Watchtower:Proxy:PortRoutes:Ports", "8443"));
        using var client = factory.CreateApiClient(8443);
        await factory.AddPortRouteAsync(8443, serviceName: "jellyfin", containerPort: 8096);
        await factory.AddRouteAsync(AppDomain, AccessMode.Public);
        await factory.ApplyProxyAsync();

        // The listener is ingress, so it dispatches by host exactly as it always did: the routed domain is
        // forwarded to its own upstream…
        var routed = await client.GetAsync($"https://{AppDomain}/reports", Ct);
        Assert.Equal(HttpStatusCode.OK, routed.StatusCode);
        Assert.Equal($"{AppUpstream}/reports", factory.Forwarder.Single().RequestUri?.ToString());

        // …and an unrouted host is the stranger it always was, rather than the port route's upstream.
        var stranger = await client.GetAsync("https://nas.lan/", Ct);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Single(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// The window between Kestrel binding a port route's listener and the listener state catching up. The
    /// two are republished from reload callbacks on the same section with no ordering between them, so a
    /// request really can arrive while the snapshot is stale — and on a deployment whose only listeners
    /// are port routes, a stale snapshot carries no ingress ports at all, which used to make the request
    /// fall through to Watchtower's own SPA on a port published to the LAN.
    /// </summary>
    /// <remarks>
    /// The staleness is injected rather than raced for: the whole point is that the ordering is not
    /// something a test can reliably provoke, which is also why the fix does not depend on it. What is
    /// asserted is the disagreement itself — table and section say "port route", snapshot says nothing —
    /// resolving towards the section, which is the data Kestrel used to create the listener.
    /// </remarks>
    [Fact]
    public async Task WhileTheListenerStateIsStale_APortRouteStillForwards() {
        using var factory = PortRoutesOnlyEstate();
        using var client = factory.CreateApiClient(PortRoutePort);
        await factory.AddPortRouteAsync(PortRoutePort, serviceName: "jellyfin", containerPort: 8096);
        await factory.ApplyProxyAsync();

        // The reading an instant before the state catches up: nothing bound, nothing ingress.
        factory.Services.GetRequiredService<YarpListenerState>().Publish(
            new YarpListenerSnapshot { ManagementPort = WatchtowerApiFactory.ManagementPort });

        var response = await client.GetAsync("https://nas.lan/web/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RecordingHttpForwarder.MarkerBody, await Body(response));
        Assert.Equal($"{PortUpstream}/web/", factory.Forwarder.Single().RequestUri?.ToString());
    }

    /// <summary>
    /// The inverse, which is what keeps consulting the section from becoming a hole: a port the
    /// projection <em>dropped</em> is absent from the section too, so asking it cannot reinstate the
    /// route. Asked with the snapshot carrying the port as ingress but not as a port route's — which is
    /// not a lag at all but the permanent steady state of a collision, and therefore the reading the
    /// hot path really lives with.
    /// </summary>
    [Fact]
    public async Task WhenTheSectionDoesNotNameThePort_ItIsNotAPortRouteListener() {
        // The row says 8443 and the projection dropped it: the TLS ingress listener has that port.
        using var factory = WatchtowerApiFactory.WithIngress(
            ("Watchtower:Proxy:PortRoutes:Ports", "8443"));
        using var client = factory.CreateApiClient(8443);
        await factory.AddPortRouteAsync(8443, serviceName: "jellyfin", containerPort: 8096);
        await factory.ApplyProxyAsync();

        factory.Services.GetRequiredService<YarpListenerState>().Publish(
            new YarpListenerSnapshot {
                ManagementPort = WatchtowerApiFactory.ManagementPort,
                IngressPorts = new HashSet<int> { 8081, 8443 },
            });

        var response = await client.GetAsync("https://nas.lan/web/", Ct);

        // The bare 404 an unrouted host gets on ingress — not the port route's upstream, and not
        // Watchtower's own pipeline.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await Body(response));
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// The other half of the section being consulted unconditionally: a port it names whose route row
    /// this instance has not projected yet — another instance created the route, the setting reached the
    /// configuration and Kestrel bound the listener, and the change signal has not been acted on here.
    /// The listener is a port route's, so the only thing that may be served on it is its own route; a
    /// fall-through would put Watchtower's own pipeline on a port published to the LAN.
    /// </summary>
    /// <remarks>
    /// Asked for <c>/health</c> rather than an application path, because that is what tells the two
    /// outcomes apart: Watchtower answers it 200 with a body, so a fall-through is loud. An arbitrary
    /// path would be a 404 either way — from the dispatcher's refusal or from the SPA fallback finding no
    /// file — and the test would pass without proving anything.
    /// </remarks>
    [Fact]
    public async Task WhenTheSectionNamesAPortTheTableDoesNot_ItIs404() {
        using var factory = PortRoutesOnlyEstate();
        using var client = factory.CreateApiClient(PortRoutePort);
        // No port route row at all, and the listener state as empty as it is before the first projection.
        await factory.ApplyProxyAsync();
        factory.Services.GetRequiredService<YarpListenerState>().Publish(
            new YarpListenerSnapshot { ManagementPort = WatchtowerApiFactory.ManagementPort });

        var response = await client.GetAsync("https://nas.lan/health", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("healthy", await Body(response), StringComparison.Ordinal);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// The request path is provider-blind by construction — the dispatch middleware is registered
    /// unconditionally and branches on the local port before it asks anything about a host — and since
    /// the ADR-0033 addendum that is load-bearing rather than incidental: a Caddy or Cloudflare
    /// deployment really does serve port routes now. Pinned here so a future gate on the provider fails
    /// a test rather than a LAN.
    /// </summary>
    [Theory]
    [InlineData("caddy")]
    [InlineData("cloudflare")]
    public async Task UnderAnotherProvider_APortRouteIsStillForwarded(string provider) {
        using var factory = WatchtowerApiFactory.WithIngress(
            ("Watchtower:Proxy:Provider", provider),
            ("Watchtower:Proxy:PortRoutes:Ports",
                PortRoutePort.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        using var client = factory.CreateApiClient(PortRoutePort);
        await factory.AddPortRouteAsync(PortRoutePort, serviceName: "jellyfin", containerPort: 8096);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"https://nas.lan:{PortRoutePort}/web/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{PortUpstream}/web/", factory.Forwarder.Single().RequestUri?.ToString());
    }

    /// <summary>The port a port route listens on in these tests, in both the settings and the row.</summary>
    private const int PortRoutePort = 9001;

    /// <summary>
    /// The shape the stale-snapshot hazard needs: both ingress ports off, so the only listeners besides
    /// the management endpoint are the port routes' — which is what makes a stale <c>IngressPorts</c>
    /// empty rather than merely incomplete.
    /// </summary>
    private static WatchtowerApiFactory PortRoutesOnlyEstate() => WatchtowerApiFactory.WithIngress(
        ("Watchtower:Proxy:Yarp:HttpPort", "0"),
        ("Watchtower:Proxy:Yarp:HttpsPort", "0"),
        ("Watchtower:Proxy:PortRoutes:Ports",
            PortRoutePort.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// An ingress host that also has a port route's listener. The setting is what the Kestrel projection
    /// derives the endpoint from, so naming it here is what gives the listener state — and therefore the
    /// dispatcher — a port to recognise.
    /// </summary>
    private static WatchtowerApiFactory PortRouteEstate() => WatchtowerApiFactory.WithIngress(
        ("Watchtower:Proxy:PortRoutes:Ports",
            PortRoutePort.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<string> Body(HttpResponseMessage response) => response.Content.ReadAsStringAsync(Ct);

    /// <summary>A projected section naming no endpoints — a host with no port routes.</summary>
    private static ProxyIngressSection EmptySection() =>
        new(new ConfigurationBuilder().Build());

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
