using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Saving the Cloudflare settings without a Zone ID (ADR-0036). The id became optional because the zones
/// can be discovered, but exactly one of the two has to be there — a save that passes with neither
/// produces a provider that will not write a single DNS record, and says so only in a background
/// reconcile nobody is watching.
/// </summary>
/// <remarks>
/// The listing is asked of the API client directly rather than of <see cref="CloudflareZoneCatalog"/>,
/// and the last test is what pins that: the catalog caches, and its cache would be answering for the
/// token this very save is replacing.
/// </remarks>
public sealed class UpdateProxyConfigCloudflareZoneTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoZoneId_IsAcceptedWhenTheTokenCanListZones() {
        using var host = AuthTestHost.Start();
        var cloudflare = new StubCloudflare { Zones = [new CloudflareZone { Id = "z1", Name = "example.com" }] };

        var result = await SaveAsync(host, CloudflareCommand(), cloudflare);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(1, cloudflare.ZoneListings);
    }

    /// <summary>
    /// A token that can see nothing is the shape of the mistake this catches: the operator dropped the
    /// Zone ID expecting discovery, and their token carries no <c>Zone:Read</c>. Both ways out are named,
    /// because either one fixes it.
    /// </summary>
    [Fact]
    public async Task NoZoneId_IsRefusedWhenTheTokenSeesNoZones() {
        using var host = AuthTestHost.Start();

        var result = await SaveAsync(host, CloudflareCommand(), new StubCloudflare { Zones = [] });

        Assert.False(result.IsSuccess);
        Assert.Contains("Zone → Zone → Read", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Zone ID", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>Cloudflare's own words, for the same reason the credential probe quotes them.</summary>
    [Fact]
    public async Task NoZoneId_IsRefusedWithCloudflaresWordsWhenTheListingFails() {
        using var host = AuthTestHost.Start();
        var cloudflare = new StubCloudflare {
            ListFailure = new HttpRequestException("Cloudflare API 403 on GET zones: 9109: Unauthorized"),
        };

        var result = await SaveAsync(host, CloudflareCommand(), cloudflare);

        Assert.False(result.IsSuccess);
        Assert.Contains("9109: Unauthorized", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Zone → Zone → Read", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pre-ADR-0036 path, byte for byte: an operator who names their zone still has it probed, so a
    /// token without DNS edit on it fails here rather than on every later reconcile.
    /// </summary>
    [Fact]
    public async Task AConfiguredZoneIdIsStillProbed() {
        using var host = AuthTestHost.Start();
        var cloudflare = new StubCloudflare { ZoneProbeReason = "10000: Authentication error" };

        var result = await SaveAsync(host, CloudflareCommand() with { CloudflareZoneId = "z1" }, cloudflare);

        Assert.False(result.IsSuccess);
        Assert.Contains("10000: Authentication error", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Zone → DNS → Edit", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A named zone is the operator's own answer to the question the listing asks, so the listing is not
    /// asked — which is what keeps a token carrying only <c>DNS:Edit</c> saveable.
    /// </summary>
    [Fact]
    public async Task AConfiguredZoneId_NeverConsultsTheZoneListing() {
        using var host = AuthTestHost.Start();
        var cloudflare = new StubCloudflare {
            ListFailure = new HttpRequestException("the token cannot list zones"),
        };

        var result = await SaveAsync(host, CloudflareCommand() with { CloudflareZoneId = "z1" }, cloudflare);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(0, cloudflare.ZoneListings);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>Everything the cloudflare provider needs, bar the zone the test is about.</summary>
    private static UpdateProxyConfig.Command CloudflareCommand() =>
        new(Enabled: true, Provider: ProxyProviderNames.Cloudflare, AdminEmail: null, CaddyImage: "caddy:2") {
            CloudflareAccountId = "account-1",
            CloudflareApiToken = "token-1",
        };

    private static async Task<Result<UpdateProxyConfig.Response>> SaveAsync(
        AuthTestHost host, UpdateProxyConfig.Command command, CloudflareApiClient cloudflare) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateProxyConfig>(scope.ServiceProvider, cloudflare);
        return await handler.HandleAsync(command, Ct);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";

    /// <summary>
    /// A Cloudflare that answers whatever the test says, without a network. A subclass rather than an
    /// interface: the seam is the handful of calls the save path makes, and every other member of the
    /// real client stays exactly what it is.
    /// </summary>
    private sealed class StubCloudflare : CloudflareApiClient {
        public IReadOnlyList<CloudflareZone> Zones { get; init; } = [];

        /// <summary>Thrown instead of answering the zone listing — the token without <c>Zone:Read</c>.</summary>
        public HttpRequestException? ListFailure { get; init; }

        /// <summary>What the zone probe complains about, or null when the token is fine for the zone.</summary>
        public string? ZoneProbeReason { get; init; }

        /// <summary>How often the zone listing was asked for — 0 is an assertion in its own right.</summary>
        public int ZoneListings { get; private set; }

        public override Task<string?> ValidateAccessAsync(
            string accountId, string token, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public override Task<string?> ValidateZoneAccessAsync(
            string zoneId, string token, CancellationToken ct = default) =>
            Task.FromResult(ZoneProbeReason);

        public override Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(
            string token, CancellationToken ct = default) {
            ZoneListings++;
            return ListFailure is null ? Task.FromResult(Zones) : Task.FromException<IReadOnlyList<CloudflareZone>>(ListFailure);
        }
    }
}
