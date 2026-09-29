using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// <c>proxy.listPrimaryDomains</c> over the wire — the base domains the create form offers routes under
/// and the Routes page groups by (ADR-0036).
/// </summary>
/// <remarks>
/// Through the JSON-RPC pipeline rather than against the handler directly, for the reason
/// <c>ProxyLanNameSuggestionTests</c> gives: the method name and the
/// <see cref="Application.Modules.Proxy.ProxyJsonContext"/> entries are exactly the two things that
/// break silently, answering "internal error" with the reason only in the log.
/// <para>
/// Only the zone catalog is substituted, so the merge, the provider gate and the parse of the stored
/// setting are all the real ones.
/// </para>
/// </remarks>
public sealed class ProxyPrimaryDomainTests {
    /// <summary>
    /// The provider decides whether zones are even a question. Under the in-process proxy there are none
    /// to ask about, and asking would mean an HTTP call per Settings visit against credentials nothing is
    /// currently acting on.
    /// </summary>
    [Fact]
    public async Task UnderTheInProcessProxy_OnlyTheConfiguredDomainsAreOffered() {
        var catalog = new StubZoneCatalog { Zones = [Zone("z1", "zone.example")] };
        using var factory = ProxyHost("yarp", "example.com", catalog);
        using var client = factory.CreateApiClient();

        var domains = await ListAsync(client);

        var only = Assert.Single(domains);
        Assert.Equal("example.com", Name(only));
        Assert.Equal("configured", only.GetProperty("source").GetString());
        Assert.Equal(0, catalog.Calls);
    }

    /// <summary>
    /// Under Cloudflare the token's zones join the list, each carrying its id — which is what lets the
    /// client say a domain is one Watchtower can write DNS into, rather than merely one somebody typed.
    /// </summary>
    [Fact]
    public async Task UnderCloudflare_TheTokensZonesJoinTheConfiguredDomains() {
        var catalog = new StubZoneCatalog { Zones = [Zone("z1", "zone.example")] };
        using var factory = ProxyHost("cloudflare", "typed.example", catalog);
        using var client = factory.CreateApiClient();

        var domains = await ListAsync(client);

        Assert.Equal(["typed.example", "zone.example"], domains.Select(Name));
        var zone = Assert.Single(domains, d => Name(d) == "zone.example");
        Assert.Equal("cloudflare-zone", zone.GetProperty("source").GetString());
        Assert.Equal("z1", zone.GetProperty("zoneId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(zone.GetProperty("detail").GetString()));
    }

    /// <summary>A domain the operator typed and the token can see is one domain, not two.</summary>
    [Fact]
    public async Task ADomainInBothSourcesIsOfferedOnce() {
        var catalog = new StubZoneCatalog { Zones = [Zone("z1", "example.com")] };
        using var factory = ProxyHost("cloudflare", "example.com", catalog);
        using var client = factory.CreateApiClient();

        var only = Assert.Single(await ListAsync(client));
        Assert.Equal("example.com", Name(only));
        Assert.Equal("configured", only.GetProperty("source").GetString());
    }

    /// <summary>
    /// A token that cannot list zones is the ordinary state of an install from before this ADR. It costs
    /// suggestions, not a working page — the call answers with what is left.
    /// </summary>
    [Fact]
    public async Task ATokenThatCanDiscoverNothing_IsNotAnError() {
        using var factory = ProxyHost("cloudflare", "example.com", new StubZoneCatalog());
        using var client = factory.CreateApiClient();

        var only = Assert.Single(await ListAsync(client));
        Assert.Equal("example.com", Name(only));
    }

    /// <summary>
    /// Nothing configured and nothing discovered is an empty list rather than an error: the create form
    /// falls back to asking for a whole hostname, exactly as it did before ADR-0036.
    /// </summary>
    [Fact]
    public async Task NothingToOfferIsAnEmptyList() {
        using var factory = ProxyHost("yarp", "", new StubZoneCatalog());
        using var client = factory.CreateApiClient();

        Assert.Empty(await ListAsync(client));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private static CloudflareZone Zone(string id, string name) => new() { Id = id, Name = name };

    private static string? Name(JsonElement domain) => domain.GetProperty("name").GetString();

    private static WatchtowerApiFactory ProxyHost(
        string provider, string primaryDomains, StubZoneCatalog catalog) => new([
            ("Watchtower:Proxy:Enabled", "false"),
            ("Watchtower:Proxy:Provider", provider),
            ("Watchtower:Proxy:PrimaryDomains", primaryDomains),
        ]) {
        // Registered last, so it wins over the host's own singleton.
        AdditionalServices = services => services.AddSingleton<CloudflareZoneCatalog>(catalog),
    };

    private static async Task<IReadOnlyList<JsonElement>> ListAsync(HttpClient client) {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync(
            "/rpc",
            new { jsonrpc = "2.0", method = "proxy.listPrimaryDomains", @params = new { }, id = "1" },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.GetProperty("result").GetProperty("domains").EnumerateArray()
            .Select(d => d.Clone())];
    }

    /// <summary>
    /// A catalog that answers with what the test says instead of calling Cloudflare. Its base's
    /// collaborators are never reached — <see cref="ListAsync"/> is the whole of its behaviour — so they
    /// are not supplied.
    /// </summary>
    private sealed class StubZoneCatalog() : CloudflareZoneCatalog(null!, null!, null!, null!) {
        public IReadOnlyList<CloudflareZone> Zones { get; init; } = [];

        /// <summary>How often the catalog was asked — 0 is an assertion in its own right.</summary>
        public int Calls { get; private set; }

        public override Task<IReadOnlyList<CloudflareZone>> ListAsync(CancellationToken ct = default) {
            Calls++;
            return Task.FromResult(Zones);
        }
    }
}
