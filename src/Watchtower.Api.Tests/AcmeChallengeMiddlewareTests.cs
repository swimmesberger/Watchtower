using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The ACME HTTP-01 responder (ADR-0022). Everything here is about <em>reachability</em>:
/// the CA calls this path over plain HTTP, on the domain being validated, as an unauthenticated stranger,
/// and every one of Watchtower's own defences would otherwise have something to say about that.
/// </summary>
/// <remarks>
/// Which is why the middleware sits first in the pipeline. A challenge that is forwarded to an upstream,
/// redirected to HTTPS, or answered with a login page is a certificate that is never issued — and the
/// failure would surface as an expiry weeks later rather than as anything an operator could connect to the
/// cause.
/// </remarks>
public sealed class AcmeChallengeMiddlewareTests {
    private const string AuthHost = "watchtower.example.invalid";
    private const string AppDomain = "app.example.invalid";
    private const string Token = "n3Xk9-tokenvalue_A";
    private const string KeyAuthorization = "n3Xk9-tokenvalue_A.7HdE2Q0bLm4XyZ8fJ1rTuV6wS3pN5cA9gK0iM2oQ4eU";

    private static string Challenge(string token) => $"/.well-known/acme-challenge/{token}";

    /// <summary>
    /// Answered on a TLS route host, over plain HTTP, with the redirect enabled — the exact combination
    /// the CA arrives in, and the one where a 302 to HTTPS would break issuance for a domain that has no
    /// certificate yet.
    /// </summary>
    [Fact]
    public async Task AKnownToken_IsServedOnAnyHost() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public, tlsEnabled: true);
        await factory.ApplyProxyAsync();
        using var published = Publish(factory);

        foreach (var url in new[] {
            $"http://{AppDomain}{Challenge(Token)}",
            $"https://{AppDomain}{Challenge(Token)}",
            $"http://nobody.example.invalid{Challenge(Token)}",
        }) {
            var response = await client.GetAsync(url, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            // Byte-exact: no trailing newline, whatever a text-writing helper would have added.
            Assert.Equal(KeyAuthorization, await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }

        Assert.Empty(factory.Forwarder.Forwarded);
    }

    [Fact]
    public async Task AnUnknownToken_Is404_AndIsNotForwarded() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public, tlsEnabled: false);
        await factory.ApplyProxyAsync();

        var response = await client.GetAsync($"http://{AppDomain}{Challenge("never-issued")}", Ct);

        // Not a fall-through: forwarding it would let any stranger ask "is this domain proxied, and by
        // what?" simply by requesting a token that was never minted.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>
    /// The bare prefix and a nested path are not challenge URLs at all, and an upstream may legitimately
    /// serve its own <c>/.well-known</c> tree — so those pass through to the route as any other path does.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/acme-challenge")]
    [InlineData("/.well-known/acme-challenge/")]
    [InlineData("/.well-known/acme-challenge/nested/token")]
    [InlineData("/.well-known/security.txt")]
    public async Task NestedOrEmptyToken_FallsThrough(string path) {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Public, tlsEnabled: false);
        await factory.ApplyProxyAsync();
        using var published = Publish(factory);

        var response = await client.GetAsync($"http://{AppDomain}{path}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RecordingHttpForwarder.MarkerBody, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The responder is ahead of the access check as well as the redirect: a restricted route's login
    /// redirect would send the CA to a page it will not follow, and the domain would never be validated.
    /// </summary>
    [Fact]
    public async Task ReachableWithAuthEnabledOnARestrictedRoute() {
        using var factory = WatchtowerApiFactory.WithYarpProxy(
            ("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", AuthHost));
        using var client = factory.CreateApiClient();
        await factory.AddRouteAsync(AppDomain, AccessMode.Restricted, tlsEnabled: true);
        await factory.ApplyProxyAsync();
        using var published = Publish(factory);

        var response = await client.GetAsync($"http://{AppDomain}{Challenge(Token)}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(KeyAuthorization, await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(factory.Forwarder.Forwarded);
    }

    /// <summary>Disposal retracts the answer, so a settled order leaves nothing answerable behind.</summary>
    [Fact]
    public async Task RetractedTokenStopsBeingAnswered() {
        using var factory = WatchtowerApiFactory.WithYarpProxy();
        using var client = factory.CreateApiClient();
        var store = factory.Services.GetRequiredService<AcmeHttpChallengeStore>();

        var published = store.Publish(Token, KeyAuthorization);
        Assert.Equal(1, store.Count);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Challenge(Token), Ct)).StatusCode);

        published.Dispose();
        // Idempotent: an order that fails after the using has already unwound must not throw here.
        published.Dispose();

        Assert.Equal(0, store.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Challenge(Token), Ct)).StatusCode);
    }

    private static IDisposable Publish(WatchtowerApiFactory factory) =>
        factory.Services.GetRequiredService<AcmeHttpChallengeStore>().Publish(Token, KeyAuthorization);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
