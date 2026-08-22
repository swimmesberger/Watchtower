using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Api.Proxy;
using Yarp.ReverseProxy.Forwarder;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Host dispatch for the in-process proxy (ADR-0020): which requests leave for a container,
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

        // The login host is in the table — it needs a certificate — but marked Local: Watchtower serves it
        // itself, and forwarding it would be forwarding to ourselves.
        var response = await client.GetAsync($"https://{AuthHost}/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await Body(response), StringComparison.Ordinal);
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// Serving itself is not the same as serving itself over anything. The login host gets the same upgrade
    /// a route host does — as it did under Caddy, whose self-route was an ordinary TLS site — because this
    /// is where the central session cookie is set, and a page reached over plain HTTP would set it without
    /// its Secure attribute.
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
