using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services.PortRoutes;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// One address the Settings page offers to add to the LAN names, as a chip.
/// </summary>
/// <param name="Value">
/// The name or address itself, in the exact spelling that goes into the setting — the client appends it
/// verbatim, so it is canonicalised here and guaranteed to be one the Save accepts.
/// </param>
/// <param name="Kind"><c>hostname</c> or <c>ip</c>.</param>
/// <param name="Source">Where it was learned — <c>reverse-dns</c>, <c>forward-dns</c>, <c>docker-host</c>
/// or <c>docker-search-domain</c>.</param>
/// <param name="Verified">Whether forward and reverse resolution agree about it.</param>
/// <param name="Detail">One sentence saying where it came from, shown as the chip's tooltip.</param>
public sealed record LanNameCandidateDto(
    string Value,
    string Kind,
    string Source,
    bool Verified,
    string Detail);

/// <summary>
/// Suggests the LAN names this deployment appears to answer on, so a hobby operator gets a working
/// certificate without typing addresses into the LAN names setting of ADR-0033 decision 6.
/// </summary>
/// <remarks>
/// Read-only and advisory in the strong sense: nothing is saved, nothing is created, and a candidate is
/// only ever an offer. The operator clicks the ones they recognise, and the setting is written by the
/// ordinary Save — which is the difference between a convenience and a certificate naming an address
/// nobody chose.
/// <para>
/// Never fails for a reason the operator cannot act on: every source is fail-open, so a daemon that is
/// not there or a resolver that answers nothing produces fewer chips rather than an error under a field
/// they were about to type in.
/// </para>
/// </remarks>
[Handler("proxy.suggestLanNames")]
public sealed class SuggestLanNames(
    IOptionsMonitor<WatchtowerOptions> options, LanNameSuggestions suggestions)
    : IHandler<SuggestLanNames.Query, Result<SuggestLanNames.Response>> {
    /// <param name="Hint">
    /// The host the browser reached this page with, without its port — the one address known for certain
    /// to work, and the only thing the server cannot find out for itself. It comes back as a candidate
    /// like any other, first in the list and held to the same rules, so a client never has to decide for
    /// itself whether an address it can display is one a certificate can name.
    /// </param>
    public sealed record Query(string? Hint);

    public sealed record Response(IReadOnlyList<LanNameCandidateDto> Candidates);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(query);
        var configured = options.CurrentValue.Proxy.PortRoutes.LanNames;
        var candidates = await suggestions.SuggestAsync(query.Hint, configured, ct);
        return new Response([
            .. candidates.Select(c => new LanNameCandidateDto(
                c.Value, c.Kind, c.Source, c.Verified, c.Detail)),
        ]);
    }
}
