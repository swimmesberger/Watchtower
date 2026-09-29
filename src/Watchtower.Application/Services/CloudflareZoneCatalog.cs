using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// The Cloudflare zones the configured API token can see (ADR-0036), cached briefly. Two callers ask:
/// <c>proxy.listPrimaryDomains</c>, so the create form can offer the domains an operator already owns,
/// and <see cref="CloudflareTunnelProvider"/>, which writes each route's DNS record into the zone whose
/// name is the longest suffix of its hostname.
/// </summary>
/// <remarks>
/// <para>
/// Fail-open by construction, because <c>Zone:Read</c> is a permission a token from before ADR-0036 does
/// not carry and asking for it is a manual step in someone's dashboard. A listing that fails is not an
/// error anybody can act on in the moment: it becomes the configured Zone ID alone — named by reading a
/// DNS record's <c>zone_name</c>, since a token that cannot list zones can usually still read the one it
/// writes into — or nothing at all. What that costs is a create form with fewer suggestions and a DNS
/// write that falls back to the configured zone, which is precisely the pre-ADR-0036 behaviour.
/// </para>
/// <para>
/// Cached for five minutes and keyed on the credentials, not merely time-boxed: the Settings page can
/// change the account, the zone or the token, and an answer computed for the previous token is about a
/// different Cloudflare account. Keying on them means a save is reflected on the next call rather than
/// up to five minutes later, without an options subscription to keep in step.
/// </para>
/// </remarks>
public class CloudflareZoneCatalog(
    CloudflareApiClient api,
    IOptionsMonitor<WatchtowerOptions> options,
    TimeProvider time,
    ILogger<CloudflareZoneCatalog> logger) {
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<CloudflareZone>? _cached;
    private string _cachedKey = "";
    private DateTimeOffset _cachedAt;

    /// <summary>
    /// The zones the current settings' token can see; empty when nothing could be discovered and no Zone
    /// ID is configured. Listed at most once per five minutes per credential set.
    /// </summary>
    public virtual async Task<IReadOnlyList<CloudflareZone>> ListAsync(CancellationToken ct = default) {
        var cf = options.CurrentValue.Proxy.Cloudflare;
        if (string.IsNullOrWhiteSpace(cf.ApiToken)) return [];
        var key = $"{cf.AccountId}|{cf.ZoneId}|{cf.ApiToken}";

        await _gate.WaitAsync(ct);
        try {
            if (_cached is not null
                && string.Equals(_cachedKey, key, StringComparison.Ordinal)
                && time.GetUtcNow() - _cachedAt < CacheTtl)
                return _cached;
            _cached = await DiscoverAsync(cf, ct);
            _cachedKey = key;
            _cachedAt = time.GetUtcNow();
            return _cached;
        } finally {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<CloudflareZone>> DiscoverAsync(
        CloudflareProxyOptions cf, CancellationToken ct) {
        try {
            return await api.ListZonesAsync(cf.ApiToken!, ct);
        } catch (HttpRequestException ex) {
            // The ordinary case for an install that predates ADR-0036: the token carries DNS:Edit and no
            // Zone:Read. The configured zone still works, so it is reported as the one known zone —
            // named if Cloudflare will say what it is called, and nameless otherwise, which never matches
            // a hostname but is still the fallback every DNS write lands in.
            if (string.IsNullOrWhiteSpace(cf.ZoneId)) {
                logger.LogWarning(ex,
                    "Could not list the Cloudflare zones; no zone id is configured either, so no primary "
                    + "domain can be discovered. Grant the API token Zone:Read, or set the zone id.");
                return [];
            }
            logger.LogInformation(ex,
                "Could not list the Cloudflare zones; falling back to the configured zone {ZoneId}. "
                + "Grant the API token Zone:Read to publish routes across several zones.", cf.ZoneId);
            string? name = null;
            try {
                name = await api.GetZoneNameAsync(cf.ZoneId!, cf.ApiToken!, ct);
            } catch (HttpRequestException nameEx) {
                logger.LogDebug(nameEx, "Could not read the name of the configured Cloudflare zone.");
            }
            return [new CloudflareZone { Id = cf.ZoneId!.Trim(), Name = name ?? "" }];
        }
    }

    /// <summary>
    /// The id of the zone <paramref name="domain"/>'s DNS record belongs in: the listed zone whose name is
    /// the longest suffix of it. Null when no zone covers the domain.
    /// </summary>
    /// <remarks>
    /// A zone with no name is skipped rather than matched, which is what
    /// <see cref="PrimaryDomains.BestMatch"/> already does with an empty candidate — the nameless zone the
    /// fallback above produces must not silently claim every hostname, since the caller's own fallback is
    /// the same zone anyway and going through here would hide that it was a guess.
    /// </remarks>
    internal static string? ResolveZoneId(IReadOnlyList<CloudflareZone> zones, string domain) {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(domain);
        var best = PrimaryDomains.BestMatch(zones.Select(z => z.Name), domain);
        return best is null
            ? null
            : zones.First(z => string.Equals(z.Name, best, StringComparison.OrdinalIgnoreCase)).Id;
    }
}
