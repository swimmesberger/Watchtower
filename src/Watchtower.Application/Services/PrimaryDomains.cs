using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Services;

/// <summary>
/// One base domain an operator publishes under, offered so a route can be created by typing a
/// subdomain instead of a whole host name.
/// </summary>
/// <param name="Name">The domain itself, normalized by <see cref="DesiredHosts.TryNormalize"/>.</param>
/// <param name="Source">
/// Where it was learned — see <see cref="PrimaryDomainSources"/>. Two sources answer different
/// questions: a configured domain is what the operator says they publish under, a discovered zone is
/// what the provider will actually let Watchtower write DNS into.
/// </param>
/// <param name="ZoneId">
/// The provider's own id for the zone, when the domain came from one. Null for a configured domain,
/// which carries no promise that any provider can serve it.
/// </param>
/// <param name="Detail">One sentence saying where it came from, shown next to the name.</param>
public sealed record PrimaryDomain(string Name, string Source, string? ZoneId, string Detail);

/// <summary>
/// The <see cref="PrimaryDomain.Source"/> values. Constants rather than literals because the wire form
/// is what the client branches on, and a typo would silently produce a domain nothing renders.
/// </summary>
public static class PrimaryDomainSources {
    /// <summary>Typed into the primary-domains setting by the operator.</summary>
    public const string Configured = "configured";

    /// <summary>Discovered by listing the zones the configured Cloudflare token can read.</summary>
    public const string CloudflareZone = "cloudflare-zone";
}

/// <summary>
/// The suffix arithmetic behind primary domains — ADR-0036. Pure and static, because the same three
/// questions are asked from four places that must agree: the settings validation, the zone the DNS
/// record is written into, the group a route is listed under, and the host the create form composes.
/// </summary>
/// <remarks>
/// <para>
/// A primary domain is a convenience, never a constraint. Nothing here is persisted and nothing here
/// decides whether a route may exist: a host that no primary domain covers is still a perfectly good
/// route, it is simply listed under "other". That is why <see cref="Compose"/> validates nothing —
/// <see cref="DesiredHosts.TryNormalize"/> in <c>proxy.createRoute</c> remains the only gate, and a
/// second, weaker copy of its rules here would only differ from it.
/// </para>
/// <para>
/// Coverage is deliberately label-wise rather than a string suffix test. <c>notexample.com</c> ends
/// with <c>example.com</c> and has nothing to do with it; treating it as covered would file a stranger's
/// domain under the operator's, and — worse, once <see cref="BestMatch"/> picks the DNS zone — try to
/// write a record for it into a zone that does not own it.
/// </para>
/// <para>
/// Everything compares <see cref="StringComparison.OrdinalIgnoreCase"/>. Host names are
/// case-insensitive, both sides arrive from normalizers that lowercase, and the one place a raw
/// spelling can still reach these calls is a host typed by hand.
/// </para>
/// </remarks>
public static class PrimaryDomains {
    /// <summary>
    /// Reads the primary-domains setting into the domains it names.
    /// </summary>
    /// <remarks>
    /// Entries are separated by commas or newlines and deduplicated case-insensitively, keeping the
    /// order they were written — the operator's own ordering is the one the Settings page and the create
    /// form's picker show back to them.
    /// <para>
    /// A junk entry fails the whole parse rather than being dropped, the same contract
    /// <see cref="InternalCa.InternalCaNames.TryParseLanNames"/> holds to. Silently keeping four of five
    /// domains would surface much later as one group of routes that never appears, with nothing pointing
    /// at the typo that caused it.
    /// </para>
    /// <para>
    /// The per-entry rules are <see cref="DesiredHosts.TryNormalize"/>'s and not a set of their own: a
    /// primary domain exists to have host names built on top of it, so a domain that could never carry a
    /// route — a wildcard, an IP literal, a name with a port or a scheme on it — is not one.
    /// </para>
    /// </remarks>
    /// <param name="domains">The normalized domains; empty unless the whole parse succeeded.</param>
    /// <param name="reason">Why the parse failed, naming the offending entry; null on success.</param>
    /// <returns>Whether every entry was a domain. An empty or whitespace value parses to nothing.</returns>
    public static bool TryParse(string? raw, out IReadOnlyList<string> domains, out string? reason) {
        var parsed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        domains = parsed;
        reason = null;

        foreach (var entry in (raw ?? "").Split(
                     [',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!DesiredHosts.TryNormalize(entry, out var domain, out var rejectReason)) {
                // Several of TryNormalize's sentences already quote the offending text. Prefixing those
                // too would read as "'bad_host': 'bad_host' may only contain…", so the entry is named
                // only when the sentence does not name it.
                reason = rejectReason.Contains(entry, StringComparison.OrdinalIgnoreCase)
                    ? rejectReason
                    : $"'{entry}': {rejectReason}";
                domains = [];
                return false;
            }
            if (seen.Add(domain)) parsed.Add(domain);
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="host"/> lives under <paramref name="primary"/> — the domain itself
    /// counts, since the apex is a host an operator routes just like any subdomain of it.
    /// </summary>
    public static bool Covers(string primary, string host) {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(host);
        if (primary.Length == 0) return false;
        if (string.Equals(primary, host, StringComparison.OrdinalIgnoreCase)) return true;
        // The leading dot is what keeps notexample.com out of example.com: the boundary has to fall on a
        // label, not anywhere in the string.
        return host.Length > primary.Length + 1
               && host[host.Length - primary.Length - 1] == '.'
               && host.AsSpan(host.Length - primary.Length)
                   .Equals(primary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The domain a host belongs to when several could claim it: the longest one that covers it, so a
    /// host under both <c>example.com</c> and <c>eu.example.com</c> is filed — and its DNS record
    /// written — under the more specific of the two. Null when nothing covers it.
    /// </summary>
    /// <remarks>
    /// The result depends on the set, never on the order it arrives in. Callers assemble their
    /// candidates from a setting and a provider's zone listing, and neither ordering is meaningful; the
    /// ordinal tie-break exists only so two same-length domains cannot make the answer depend on which
    /// source answered first.
    /// </remarks>
    public static string? BestMatch(IEnumerable<string> primaries, string host) {
        ArgumentNullException.ThrowIfNull(primaries);
        ArgumentNullException.ThrowIfNull(host);

        string? best = null;
        foreach (var primary in primaries) {
            if (primary is null || !Covers(primary, host)) continue;
            if (best is null
                || primary.Length > best.Length
                || (primary.Length == best.Length && StringComparer.Ordinal.Compare(primary, best) < 0))
                best = primary;
        }
        return best;
    }

    /// <summary>
    /// The labels <paramref name="host"/> adds to <paramref name="primary"/> — the empty string for the
    /// apex itself, and null when the host does not live under the domain at all.
    /// </summary>
    /// <remarks>
    /// The empty string and null are different answers on purpose. Empty is "this route is the domain",
    /// which the create form prefills as a blank subdomain box; null is "this domain has nothing to say
    /// about this host", which sends the route to the ungrouped list.
    /// </remarks>
    public static string? Subdomain(string primary, string host) {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(host);
        if (!Covers(primary, host)) return null;
        return host.Length == primary.Length ? "" : host[..(host.Length - primary.Length - 1)];
    }

    /// <summary>
    /// Joins a subdomain onto a primary domain, undoing <see cref="Subdomain"/>: an empty subdomain
    /// yields the domain itself.
    /// </summary>
    /// <remarks>
    /// Tolerant of how the operator types into a subdomain box — surrounding whitespace and the dots
    /// they may add on either side of it are trimmed, since <c>app.</c> in a field labelled
    /// "<c>.example.com</c>" is the same intent as <c>app</c>.
    /// <para>
    /// Nothing is validated here. This composes the string the operator is about to submit, and
    /// <see cref="DesiredHosts.TryNormalize"/> in <c>proxy.createRoute</c> stays the single gate that
    /// decides whether it is a host — so a subdomain with a space or a wildcard in it is composed and
    /// then refused there, with the one message the rest of the product uses.
    /// </para>
    /// </remarks>
    public static string Compose(string? subdomain, string primary) {
        ArgumentNullException.ThrowIfNull(primary);
        var sub = (subdomain ?? "").Trim().Trim('.').Trim();
        return sub.Length == 0 ? primary : $"{sub}.{primary}";
    }
}
