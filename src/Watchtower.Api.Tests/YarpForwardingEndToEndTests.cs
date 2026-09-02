using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The forward itself, with nothing stubbed on the far end: YARP's real <see cref="IHttpForwarder"/>
/// against a real Kestrel upstream on loopback.
/// </summary>
/// <remarks>
/// Everything else in this area substitutes the forwarder, which is the right trade for asserting on the
/// <em>decisions</em> — but it means no test would notice if the copy itself were broken. This one closes
/// that: bytes leave the process, an upstream answers, and the response comes back through the dispatcher
/// to the caller with its status, headers and body intact.
/// <para>
/// <c>TestServer</c> turns out to cooperate: the forwarder needs <c>IHttpResponseBodyFeature</c> to stream
/// the upstream's response back, and the test host provides one. The upstream is an ordinary
/// <c>WebApplication</c> on <c>127.0.0.1:0</c>, so the route's "container alias" is a loopback address and
/// port that really exists for the duration of the test.
/// </para>
/// </remarks>
public sealed class YarpForwardingEndToEndTests {
    [Fact]
    public async Task ARequestAndItsResponse_RoundTripThroughTheRealForwarder() {
        await using var upstream = await EchoUpstream.StartAsync();
        using var factory = new WatchtowerApiFactory(
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", "yarp")) {
            UseRealForwarder = true,
        };
        using var client = factory.CreateApiClient();
        SeedLoopbackRoute(factory, upstream.Port);

        var request = new HttpRequestMessage(HttpMethod.Post, $"http://{Domain}/echo?q=1") {
            Content = new StringContent("hello upstream", Encoding.UTF8, "text/plain"),
        };
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Set by the upstream, so its presence proves the response really came from there.
        Assert.Equal("yes", Assert.Single(response.Headers.GetValues("X-Echo")));
        var body = await response.Content.ReadAsStringAsync(Ct);
        // The upstream reports what it was asked, which is how the outgoing shape is checked from the far side.
        Assert.Contains("method=POST", body, StringComparison.Ordinal);
        Assert.Contains("path=/echo", body, StringComparison.Ordinal);
        Assert.Contains("query=?q=1", body, StringComparison.Ordinal);
        // The header YARP's default transformer would have dropped, seen by the application that needs it.
        Assert.Contains($"host={Domain}", body, StringComparison.Ordinal);
        Assert.Contains($"x-forwarded-host={Domain}", body, StringComparison.Ordinal);
        Assert.Contains("x-forwarded-proto=http", body, StringComparison.Ordinal);
        // And the request body travelled too, not just the headers.
        Assert.Contains("body=hello upstream", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A streamed response arrives in pieces rather than as one buffered blob. The proxy must not sit on
    /// the first chunk waiting for the last — an SSE stream or a log tail behind the proxy would otherwise
    /// look frozen, which is exactly the class of bug a buffering proxy introduces.
    /// </summary>
    [Fact]
    public async Task AStreamedResponse_IsNotBuffered() {
        await using var upstream = await EchoUpstream.StartAsync();
        using var factory = new WatchtowerApiFactory(
            ("Watchtower:Proxy:Enabled", "true"), ("Watchtower:Proxy:Provider", "yarp")) {
            UseRealForwarder = true,
        };
        using var client = factory.CreateApiClient();
        SeedLoopbackRoute(factory, upstream.Port);

        // The upstream writes the first chunk, then waits to be released before writing the second.
        var response = await client.GetAsync(
            $"http://{Domain}/stream", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer, Ct);
        // Reached the client while the upstream is still holding the response open.
        Assert.Equal("first\n", Encoding.UTF8.GetString(buffer, 0, read));

        upstream.Release();
        var rest = await new StreamReader(stream).ReadToEndAsync(Ct);
        Assert.Equal("second\n", rest);
    }

    /// <summary>
    /// The same round trip on a port-bound route's listener (ADR-0033). Its forward path is a separate
    /// branch of the dispatcher — no access check, no reserved prefix, no upgrade — so "the bytes still
    /// travel" has to be proven there too rather than inherited from the host path.
    /// </summary>
    [Fact]
    public async Task APortRoute_RoundTripsThroughTheRealForwarder() {
        await using var upstream = await EchoUpstream.StartAsync();
        // WithIngress's shape, spelled out because the real forwarder has to be selected with it.
        using var factory = new WatchtowerApiFactory(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
            ("Watchtower:Proxy:PortRoutes:Ports", "9001")) {
            UseRealProxyProvider = true,
            HasIngress = true,
            UseRealForwarder = true,
        };
        using var client = factory.CreateApiClient(9001);
        SeedLoopbackPortRoute(factory, upstream.Port);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://nas.lan/echo?q=1") {
            Content = new StringContent("hello upstream", Encoding.UTF8, "text/plain"),
        };
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("yes", Assert.Single(response.Headers.GetValues("X-Echo")));
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("method=POST", body, StringComparison.Ordinal);
        Assert.Contains("query=?q=1", body, StringComparison.Ordinal);
        Assert.Contains("body=hello upstream", body, StringComparison.Ordinal);
        // The address the client dialled, echoed back — the route has no hostname to substitute — and
        // https, because this listener terminates TLS and does nothing else.
        Assert.Contains("host=nas.lan", body, StringComparison.Ordinal);
        Assert.Contains("x-forwarded-host=nas.lan", body, StringComparison.Ordinal);
        Assert.Contains("x-forwarded-proto=https", body, StringComparison.Ordinal);
    }

    // ── Estate ────────────────────────────────────────────────────────────────

    private const string Domain = "e2e.example.invalid";

    /// <summary>The port-route counterpart of <see cref="SeedLoopbackRoute"/>, for the same reason.</summary>
    private static void SeedLoopbackPortRoute(WatchtowerApiFactory factory, int port) =>
        factory.Services.GetRequiredService<ProxyRouteTable>().PublishPortRoutes(
            [new ProxyPortSite(9001, "127.0.0.1", port, RouteId: 1)]);

    /// <summary>
    /// Points the routing table at the loopback upstream directly, rather than seeding a route and letting
    /// the projection build the address.
    /// </summary>
    /// <remarks>
    /// The projection names a Docker DNS alias (<see cref="ProxyIngressNetworks.EdgeAlias"/> — hyphen-joined
    /// project and service), and no arrangement of those two names spells <c>127.0.0.1</c>. Which is fine:
    /// what these two tests are about is the hop, and the projection that decides <em>where</em> the hop
    /// goes has its own tests. Writing the row is also all the estate they need — a public route consults
    /// no database at request time.
    /// </remarks>
    private static void SeedLoopbackRoute(WatchtowerApiFactory factory, int port) =>
        factory.Services.GetRequiredService<ProxyRouteTable>().PublishHostRoutes(
            [new ProxySite(Domain, "127.0.0.1", port, Tls: false)]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A real HTTP server on loopback that reports what it was asked and can stream on demand.</summary>
    private sealed class EchoUpstream : IAsyncDisposable {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource _release;

        private EchoUpstream(WebApplication app, int port, TaskCompletionSource release) {
            _app = app;
            Port = port;
            _release = release;
        }

        /// <summary>The port the kernel handed out.</summary>
        public int Port { get; }

        /// <summary>Lets the streaming endpoint write its second chunk.</summary>
        public void Release() => _release.TrySetResult();

        public static async Task<EchoUpstream> StartAsync() {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();

            // Captured as a local rather than reached for through the instance: the endpoint's closure would
            // otherwise depend on an assignment that has not happened when the delegate is written.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            app.MapMethods("/echo", ["GET", "POST"], async (HttpContext http) => {
                var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
                var report = new StringBuilder()
                    .Append("method=").Append(http.Request.Method).Append('\n')
                    .Append("path=").Append(http.Request.Path).Append('\n')
                    .Append("query=").Append(http.Request.QueryString).Append('\n')
                    .Append("host=").Append(http.Request.Host.Value).Append('\n')
                    .Append("x-forwarded-host=").Append(http.Request.Headers["X-Forwarded-Host"]).Append('\n')
                    .Append("x-forwarded-proto=").Append(http.Request.Headers["X-Forwarded-Proto"]).Append('\n')
                    .Append("body=").Append(body).Append('\n');
                http.Response.Headers["X-Echo"] = "yes";
                http.Response.ContentType = "text/plain";
                await http.Response.WriteAsync(report.ToString());
            });
            app.MapGet("/stream", async (HttpContext http) => {
                http.Response.ContentType = "text/plain";
                await http.Response.WriteAsync("first\n");
                await http.Response.Body.FlushAsync();
                await release.Task;
                await http.Response.WriteAsync("second\n");
            });

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First();
            return new EchoUpstream(app, new Uri(address).Port, release);
        }

        public async ValueTask DisposeAsync() {
            Release();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
