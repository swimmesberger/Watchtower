using System.Net;
using Watchtower.Application.Entities;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The on-demand-TLS gate <c>GET /api/proxy/ask</c>, from both sides of the hop it distinguishes: Caddy's
/// own direct call from its TLS machinery, and the same path reached through a <c>reverse_proxy</c> site.
/// </summary>
/// <remarks>
/// The answer is a route-existence oracle, and the login-host self-routes proxy every path to this app, so
/// the endpoint is reachable by anyone who can reach any login page. The property under test is therefore
/// not just "proxied callers are refused" but that they are refused <em>indistinguishably</em>: a known and
/// an unknown domain have to produce the same response, or the refusal is still an oracle.
/// <para>
/// All of that presupposes the one caller it exists for, so every host here selects Caddy explicitly. Under
/// the other two providers nothing asks, and the endpoint is not there at all — the last test below.
/// </para>
/// </remarks>
public sealed class ProxyAskTests {
    private const string KnownDomain = "app.example.invalid";
    private const string UnknownDomain = "nobody.example.invalid";

    private static string Ask(string domain) => $"/api/proxy/ask?domain={domain}";

    /// <summary>A host with Caddy as the selected provider — the only one this endpoint answers under.</summary>
    private static WatchtowerApiFactory Caddy() => new(("Watchtower:Proxy:Provider", "caddy"));

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.7")]
    [InlineData("X-Forwarded-Host", "watchtower.example.invalid")]
    public async Task AProxiedRequest_CannotTellAKnownDomainFromAnUnknownOne(string header, string value) {
        using var factory = Caddy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(KnownDomain, AccessMode.Public);

        var known = await Send(client, Ask(KnownDomain), header, value);
        var unknown = await Send(client, Ask(UnknownDomain), header, value);

        // Identical, and identical to what a path that does not exist would answer.
        Assert.Equal(HttpStatusCode.NotFound, known.StatusCode);
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Empty(await known.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Caddy stamps all of them at once, which is the shape that actually arrives; the single-header cases
    /// above only establish that either one is enough on its own.
    /// </summary>
    [Fact]
    public async Task TheFullForwardingHeaderSet_IsRefusedToo() {
        using var factory = Caddy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(KnownDomain, AccessMode.Public);

        var request = new HttpRequestMessage(HttpMethod.Get, Ask(KnownDomain));
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Host", "watchtower.example.invalid");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The direct call — Caddy's TLS module over the control network — keeps answering exactly as it did:
    /// 200 for a domain in the route table, 403 for one that is not, 400 without a domain at all. This is
    /// the half that on-demand certificate issuance depends on, so the gate must not have narrowed it.
    /// </summary>
    [Fact]
    public async Task AnUnmarkedRequest_KeepsTodaysAnswers() {
        using var factory = Caddy();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(KnownDomain, AccessMode.Public);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Ask(KnownDomain), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Ask(UnknownDomain), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/proxy/ask", ct)).StatusCode);
    }

    /// <summary>
    /// Under any other provider the endpoint is simply not there. It exists for Caddy's on-demand-TLS
    /// module and nothing else: the in-process proxy holds the route table in memory, and Cloudflare's edge
    /// terminates TLS and never asks whether a hostname is known — so under either of those it would be a
    /// route-existence oracle with no consumer at all.
    /// </summary>
    [Theory]
    [InlineData("yarp")]
    [InlineData("cloudflare")]
    public async Task UnderAnotherProvider_TheEndpointIsNotThere(string provider) {
        using var factory = new WatchtowerApiFactory(("Watchtower:Proxy:Provider", provider));
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;
        await factory.AddRouteAsync(KnownDomain, AccessMode.Public);

        // Indistinguishable from a path that was never mapped, and identical for a known and an unknown
        // domain — a 403 for the unknown one would still be the oracle this gate exists to close.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Ask(KnownDomain), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Ask(UnknownDomain), ct)).StatusCode);
        // Not even the shape of the request is judged first: no domain at all is still a 404, not a 400.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/proxy/ask", ct)).StatusCode);
    }

    private static Task<HttpResponseMessage> Send(
        HttpClient client, string url, string header, string value) {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(header, value);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
