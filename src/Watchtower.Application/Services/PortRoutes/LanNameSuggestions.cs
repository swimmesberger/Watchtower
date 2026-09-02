using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.InternalCa;

namespace Watchtower.Application.Services.PortRoutes;

/// <summary>
/// One address this deployment might answer on, offered to the operator for the LAN names setting.
/// </summary>
/// <param name="Value">
/// The name or address itself, canonicalised — guaranteed to be something
/// <see cref="InternalCaNames.TryParseLanNames"/> accepts, so a chip that is clicked cannot produce a
/// setting the Save refuses.
/// </param>
/// <param name="Kind"><c>hostname</c> or <c>ip</c> — which kind of subject alternative name it becomes.</param>
/// <param name="Source">Where it was learned: <c>reverse-dns</c>, <c>forward-dns</c>, <c>docker-host</c>
/// or <c>docker-search-domain</c>.</param>
/// <param name="Verified">
/// Whether forward and reverse resolution agree about it. A suggestion is worth offering without this —
/// a name that only the operator's own machine resolves is still the name they type — but it is the
/// difference between "this is how you get here" and "this might be".
/// </param>
/// <param name="Detail">One sentence saying where it came from, shown as the chip's tooltip.</param>
public sealed record LanNameCandidate(
    string Value,
    string Kind,
    string Source,
    bool Verified,
    string Detail);

/// <summary>
/// Works out which LAN names this deployment probably answers on, so the operator can add them with a
/// click instead of typing them (ADR-0033 decision 6).
/// </summary>
/// <remarks>
/// <para>
/// Nothing here writes anything. Every candidate is a suggestion the operator accepts or ignores, which
/// is the whole reason it can afford to guess: the cost of a wrong guess is a chip nobody clicks, and
/// the cost of a missing one is an operator typing an address by hand.
/// </para>
/// <para>
/// Two sources, and neither can see the LAN directly — Watchtower runs in a container with its own
/// network namespace, so "what addresses is this host reachable at" is not a question it can ask its own
/// interfaces. What it can ask is (1) the browser: the address the UI was reached with is, by
/// construction, an address that works, and DNS turns it into its counterpart of the other kind; and (2)
/// the Docker daemon, whose <c>/info</c> carries the <em>host's</em> hostname, which on a home LAN is
/// very often the name people type. The container's <c>/etc/resolv.conf</c> then says which search
/// domains that short name might be fully qualified in.
/// </para>
/// <para>
/// Every source is fail-open in the strong sense: an exception, a timeout, a daemon that is not there,
/// a resolver that answers nothing — each means that source contributes no candidates, never an error
/// to the client. A Settings page must not grow a red banner because a convenience could not be
/// computed.
/// </para>
/// </remarks>
public sealed class LanNameSuggestions(
    DnsPreflight dns,
    DockerEngineClient docker,
    ILogger<LanNameSuggestions> logger) {
    /// <summary>
    /// How long any one lookup gets. A resolver that is going to answer answers in milliseconds; one
    /// that is going to time out would otherwise hold the whole request for its own timeout, which on a
    /// LAN with a dead nameserver is measured in seconds per query.
    /// </summary>
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The ceiling on the whole pass. Suggestions render under a field the operator is already looking
    /// at, so the honest failure is fewer chips rather than a page that waits.
    /// </summary>
    internal static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The container's resolver configuration, whose <c>search</c> line Docker copies from the host.
    /// Settable so a test can point it at a file it wrote; there is no configuration path for it.
    /// </summary>
    internal string ResolvConfPath { get; set; } = "/etc/resolv.conf";

    /// <param name="hint">
    /// The address the browser reached the UI with, host only. Never returned as a candidate itself —
    /// the client already offers that one without asking anybody, since it holds it with certainty and
    /// this service would only be guessing at it.
    /// </param>
    /// <param name="configuredLanNames">The setting as it stands, whose entries are not offered again.</param>
    public async Task<IReadOnlyList<LanNameCandidate>> SuggestAsync(
        string? hint, string? configuredLanNames, CancellationToken ct) {
        var found = new List<Found>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TotalBudget);

        await AddFromHintAsync(hint, found, budget.Token);
        await AddFromDockerHostAsync(found, budget.Token);

        return Assemble(found, Configured(configuredLanNames));
    }

    // ── Source 1: the address the browser used ───────────────────────────────────────────────────

    private async Task AddFromHintAsync(string? hint, List<Found> found, CancellationToken ct) {
        var value = Normalize(hint);
        if (value is null || IsExcluded(value)) return;

        if (IPAddress.TryParse(value, out var address)) {
            // An address in the browser's bar, so the interesting counterpart is the name it answers to:
            // a certificate that carries only the address makes every bookmark an address.
            foreach (var name in await ReverseAsync(address, ct)) {
                var normalized = Normalize(name);
                if (normalized is null) continue;
                var forward = await ForwardAsync(normalized, ct);
                var verified = forward.Contains(address.ToString(), StringComparer.OrdinalIgnoreCase);
                found.Add(new Found(
                    normalized, "reverse-dns", verified,
                    verified
                        ? $"Reverse DNS for {value}, and the name resolves back to it."
                        : $"Reverse DNS for {value}. It does not resolve back to that address from here."));
            }
            return;
        }

        // A name in the browser's bar, so the counterpart is the address it resolves to — which is what
        // a client that ignores the name and dials the number needs the certificate to carry.
        foreach (var addressText in await ForwardAsync(value, ct)) {
            if (!IPAddress.TryParse(addressText, out var resolved)) continue;
            var names = await ReverseAsync(resolved, ct);
            var verified = names.Any(n => string.Equals(Normalize(n), value, StringComparison.OrdinalIgnoreCase));
            found.Add(new Found(
                resolved.ToString(), "forward-dns", verified,
                verified
                    ? $"{value} resolves to this address, and reverse DNS agrees."
                    : $"{value} resolves to this address."));
        }
    }

    // ── Source 2: the Docker host's own name ─────────────────────────────────────────────────────

    private async Task AddFromDockerHostAsync(List<Found> found, CancellationToken ct) {
        var host = Normalize(await DockerHostNameAsync(ct));
        if (host is null || IsExcluded(host) || IPAddress.TryParse(host, out _)) return;

        // Offered whether or not it resolves from in here. A container's resolver is Docker's embedded
        // one, which knows nothing about the host's name, while the operator's laptop may well resolve it
        // over mDNS or from the router's lease table — so "does not resolve here" is weak evidence
        // against a name the host itself is called.
        var hostResolves = (await ForwardAsync(host, ct)).Count > 0;
        found.Add(new Found(
            host, "docker-host", hostResolves,
            hostResolves
                ? "The Docker host's own name, and it resolves from here."
                : "The Docker host's own name. It does not resolve from inside this container, which "
                  + "says little about whether your browser resolves it."));

        // The same short name inside each search domain. Offered only when it resolves: an FQDN guessed
        // from two strings and confirmed by nothing is noise in a row the operator is meant to trust.
        foreach (var domain in SearchDomains()) {
            var fqdn = Normalize($"{host}.{domain}");
            if (fqdn is null || IsExcluded(fqdn)) continue;
            if ((await ForwardAsync(fqdn, ct)).Count == 0) continue;
            found.Add(new Found(
                fqdn, "docker-search-domain", true,
                $"The Docker host's name in the {domain} search domain, and it resolves."));
        }
    }

    private async Task<string?> DockerHostNameAsync(CancellationToken ct) {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookup.CancelAfter(LookupTimeout);
        try {
            return (await docker.GetEngineInfoAsync(lookup.Token)).Name;
        } catch (Exception e) {
            logger.LogDebug(e, "LAN name suggestions: the Docker daemon did not report a host name.");
            return null;
        }
    }

    /// <summary>The search domains from the container's resolver configuration, in order.</summary>
    private IReadOnlyList<string> SearchDomains() {
        try {
            return ParseSearchDomains(File.ReadAllText(ResolvConfPath));
        } catch (Exception e) {
            logger.LogDebug(e, "LAN name suggestions: {Path} could not be read.", ResolvConfPath);
            return [];
        }
    }

    // ── The pure half ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The domains a short name may be completed with, read from a <c>resolv.conf</c>'s <c>search</c>
    /// line. The last such line wins, which is what a resolver does with it; comments (<c>#</c> or
    /// <c>;</c> to end of line) are stripped first, and the six-domain cap is the resolver's own.
    /// </summary>
    internal static IReadOnlyList<string> ParseSearchDomains(string? content) {
        var domains = new List<string>();
        foreach (var raw in (content ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)) {
            var line = raw;
            var comment = line.IndexOfAny(['#', ';']);
            if (comment >= 0) line = line[..comment];
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], "search", StringComparison.OrdinalIgnoreCase)) continue;
            // A later search line replaces an earlier one rather than adding to it.
            domains.Clear();
            foreach (var part in parts.Skip(1)) {
                var domain = part.TrimEnd('.').Trim().ToLowerInvariant();
                if (domain.Length == 0 || domains.Contains(domain, StringComparer.Ordinal)) continue;
                domains.Add(domain);
                if (domains.Count == 6) break;
            }
        }
        return domains;
    }

    /// <summary>
    /// Turns what the sources found into what the operator is shown: canonicalised, filtered against the
    /// rules and against the setting as it stands, deduplicated, and verified-first.
    /// </summary>
    internal static IReadOnlyList<LanNameCandidate> Assemble(
        IEnumerable<Found> found, IReadOnlySet<string> configured) {
        var byValue = new Dictionary<string, LanNameCandidate>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var candidate in found) {
            // The parser the setting itself is validated by, run over the one value: a suggestion it
            // would refuse is a chip whose only effect is a Save that fails.
            if (!InternalCaNames.TryParseLanNames(candidate.Value, out var names, out var ips, out _)) continue;
            var (value, kind) = (names.Count, ips.Count) switch {
                (1, 0) => (names[0], "hostname"),
                (0, 1) => (ips[0].ToString(), "ip"),
                _ => (null, null),
            };
            if (value is null || kind is null) continue;
            if (IsExcluded(value) || configured.Contains(value)) continue;

            if (byValue.TryGetValue(value, out var existing)) {
                // The same address reached two ways. Verified is the better of the two answers about it,
                // and the source that verified it is the one whose sentence explains why.
                if (candidate.Verified && !existing.Verified)
                    byValue[value] = new LanNameCandidate(
                        value, kind, candidate.Source, true, candidate.Detail);
                continue;
            }
            byValue[value] = new LanNameCandidate(
                value, kind, candidate.Source, candidate.Verified, candidate.Detail);
            order.Add(value);
        }

        // Verified first, discovery order within each group — a stable order, so a refetch does not
        // reshuffle chips under the pointer.
        return [
            .. order.Select(v => byValue[v]).Where(c => c.Verified),
            .. order.Select(v => byValue[v]).Where(c => !c.Verified),
        ];
    }

    /// <summary>
    /// Everything the setting already names, in both spellings it can be written in — the parsed,
    /// canonical form, and the raw entries as typed, so a setting that does not parse at all still
    /// suppresses the names it does contain.
    /// </summary>
    internal static IReadOnlySet<string> Configured(string? raw) {
        var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (InternalCaNames.TryParseLanNames(raw, out var names, out var ips, out _)) {
            foreach (var name in names) configured.Add(name);
            foreach (var ip in ips) configured.Add(ip.ToString());
        }
        foreach (var entry in (raw ?? "").Split(
                     [',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (Normalize(entry) is { } value) configured.Add(value);
        }
        return configured;
    }

    /// <summary>
    /// Trims, drops the brackets an IPv6 authority is written in and the trailing dot of a fully
    /// qualified name, and lowercases — so the same thing arriving from two sources is one string.
    /// Null when nothing is left.
    /// </summary>
    internal static string? Normalize(string? value) {
        var trimmed = (value ?? "").Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) trimmed = trimmed[1..^1];
        if (trimmed.EndsWith('.')) trimmed = trimmed[..^1];
        trimmed = trimmed.Trim().ToLowerInvariant();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Whether a value is one nobody should be offered: an address that means "this machine" rather than
    /// "this deployment", or a name that resolves to something different on every host that asks.
    /// </summary>
    /// <remarks>
    /// Each of these would be actively harmful in a certificate the whole LAN validates against.
    /// <c>localhost</c> and the loopback range name the asking machine; a link-local address is not
    /// routed off its own segment; <c>host.docker.internal</c> and its neighbours are names Docker
    /// synthesises inside a container, meaningless to a browser. And <c>0.0.0.0</c> is a wildcard that
    /// is not an address at all.
    /// </remarks>
    internal static bool IsExcluded(string value) {
        if (IPAddress.TryParse(value, out var address)) {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal) return true;
            if (address.AddressFamily == AddressFamily.InterNetwork) {
                var octets = address.GetAddressBytes();
                if (octets[0] == 169 && octets[1] == 254) return true;
            }
            return false;
        }

        if (value is "localhost" or "docker.internal") return true;
        return value.EndsWith(".localhost", StringComparison.Ordinal)
            || value.EndsWith(".docker.internal", StringComparison.Ordinal);
    }

    /// <summary>What a source found, before the rules that decide whether it is shown.</summary>
    internal sealed record Found(string Value, string Source, bool Verified, string Detail);

    // ── Lookups ──────────────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> ForwardAsync(string host, CancellationToken ct) {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookup.CancelAfter(LookupTimeout);
        try {
            return await dns.ResolveAsync(host, lookup.Token);
        } catch (Exception e) {
            logger.LogDebug(e, "LAN name suggestions: {Host} could not be resolved.", host);
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> ReverseAsync(IPAddress address, CancellationToken ct) {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lookup.CancelAfter(LookupTimeout);
        try {
            return await dns.ResolveNamesAsync(address, lookup.Token);
        } catch (Exception e) {
            logger.LogDebug(e, "LAN name suggestions: {Address} has no reverse name here.", address);
            return [];
        }
    }
}
