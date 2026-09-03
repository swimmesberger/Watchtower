using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>One base domain the client may offer routes under (ADR-0036).</summary>
/// <param name="Name">The domain itself, in the spelling a hostname is composed on top of.</param>
/// <param name="Source">
/// <c>configured</c> or <c>cloudflare-zone</c> — see <see cref="PrimaryDomainSources"/>. The client
/// branches on it only to say where a domain came from; both kinds are offered alike.
/// </param>
/// <param name="ZoneId">The provider's id for the zone, when the domain is one; null otherwise.</param>
/// <param name="Detail">One sentence saying where it came from, shown beside the name.</param>
public sealed record PrimaryDomainDto(string Name, string Source, string? ZoneId, string Detail);

/// <summary>
/// The base domains this deployment publishes under (ADR-0036), merged from the two sources that know:
/// the primary-domains setting, and — under the Cloudflare provider — the zones the API token can see.
/// The create form offers a subdomain box under each of them and the Routes page groups by them.
/// </summary>
/// <remarks>
/// Derived, never stored: nothing here is a route and nothing here decides whether one may exist. A
/// hostname no primary domain covers is still a perfectly good route, which is why the answer is only
/// ever an offer and why <c>proxy.createRoute</c>'s own normalisation stays the single gate.
/// <para>
/// Never fails for a reason the operator cannot act on, the same contract <c>proxy.suggestLanNames</c>
/// holds to: a saved setting that no longer parses and a zone listing the token may not make both yield
/// fewer domains rather than an error under a form somebody is filling in.
/// </para>
/// </remarks>
[Handler("proxy.listPrimaryDomains")]
public sealed class ListPrimaryDomains(
    IOptionsMonitor<WatchtowerOptions> options, CloudflareZoneCatalog zones)
    : IHandler<ListPrimaryDomains.Query, Result<ListPrimaryDomains.Response>> {
    public sealed record Query;

    public sealed record Response(IReadOnlyList<PrimaryDomainDto> Domains);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var proxy = options.CurrentValue.Proxy;
        // The reason is deliberately dropped: proxy.updateConfig refuses a bad value at the moment it is
        // typed, so an unparseable one here is a stored value nobody is editing — and the form that asked
        // this question wants the domains that do work, not a complaint about one that does not.
        PrimaryDomains.TryParse(proxy.PrimaryDomains, out var configured, out _);

        IReadOnlyList<CloudflareZone> discovered = [];
        // Only under Cloudflare: the other providers have no zones, and asking would mean an HTTP call
        // per Settings visit against credentials nothing is currently acting on.
        if (proxy.ResolveProvider() == ProxyProviderKind.Cloudflare)
            discovered = await zones.ListAsync(ct);

        return new Response([
            .. PrimaryDomains.Merge(configured, discovered)
                .Select(d => new PrimaryDomainDto(d.Name, d.Source, d.ZoneId, d.Detail)),
        ]);
    }
}
