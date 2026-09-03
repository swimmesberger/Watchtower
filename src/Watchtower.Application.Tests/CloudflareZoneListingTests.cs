using System.Net;
using System.Text;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The two zone calls at the wire (ADR-0036): the URL Cloudflare is asked for and the envelope its
/// answer is read out of. Both are the kind of thing that cannot be noticed from inside the process —
/// a mistyped query string or a missing serializer entry fails identically to "the token has no
/// permission", weeks later, in a reconcile.
/// </summary>
public sealed class CloudflareZoneListingTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Active zones only and one page: a pending zone serves no DNS, and an account with more than 50
    /// zones is past the point where naming the one to use is the clearer configuration.
    /// </summary>
    [Fact]
    public async Task ListingAsksForOnePageOfActiveZones_AndReadsTheEnvelope() {
        var handler = new StubCloudflareApi("""
            {
              "success": true,
              "errors": [],
              "result": [
                { "id": "z1", "name": "example.com", "status": "active" },
                { "id": "z2", "name": "eu.example.com", "status": "active" }
              ]
            }
            """);
        using var client = new CloudflareApiClient(new HttpClient(handler));

        var zones = await client.ListZonesAsync("token-1", Ct);

        Assert.Equal(
            "https://api.cloudflare.com/client/v4/zones?status=active&per_page=50",
            handler.LastUrl);
        Assert.Equal("Bearer token-1", handler.LastAuthorization);
        Assert.Equal(["z1", "z2"], zones.Select(z => z.Id));
        Assert.Equal(["example.com", "eu.example.com"], zones.Select(z => z.Name));
        Assert.Equal("active", zones[0].Status);
    }

    /// <summary>
    /// The fallback for a token that can write a zone's records but not list zones: the zone names
    /// itself through any record in it.
    /// </summary>
    [Fact]
    public async Task TheZoneNameIsReadFromOneOfItsRecords() {
        var handler = new StubCloudflareApi("""
            {
              "success": true,
              "errors": [],
              "result": [
                {
                  "id": "r1", "type": "A", "name": "www.example.com",
                  "content": "203.0.113.7", "proxied": true, "zone_name": "example.com"
                }
              ]
            }
            """);
        using var client = new CloudflareApiClient(new HttpClient(handler));

        var name = await client.GetZoneNameAsync("z1", "token-1", Ct);

        Assert.Equal("https://api.cloudflare.com/client/v4/zones/z1/dns_records?per_page=1", handler.LastUrl);
        Assert.Equal("example.com", name);
    }

    /// <summary>An empty zone has no record to ask, which is a zone without records — not a failure.</summary>
    [Fact]
    public async Task AZoneWithNoRecordsHasNoNameToGive() {
        using var client = new CloudflareApiClient(
            new HttpClient(new StubCloudflareApi("""{ "success": true, "errors": [], "result": [] }""")));

        Assert.Null(await client.GetZoneNameAsync("z1", "token-1", Ct));
    }

    /// <summary>Cloudflare's own words travel, so the caller's log says what the API objected to.</summary>
    [Fact]
    public async Task ARefusedListingCarriesCloudflaresErrorMessage() {
        var handler = new StubCloudflareApi(
            """{ "success": false, "errors": [{ "code": 9109, "message": "Unauthorized to access requested resource" }] }""",
            HttpStatusCode.Forbidden);
        using var client = new CloudflareApiClient(new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ListZonesAsync("token-1", Ct));

        Assert.Contains("9109: Unauthorized to access requested resource", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Answers one canned body and records what it was asked.</summary>
    private sealed class StubCloudflareApi(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler {
        public string? LastUrl { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
            LastUrl = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
