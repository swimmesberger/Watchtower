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
/// <param name="Source">
/// Where it was learned: <c>browser</c>, <c>reverse-dns</c>, <c>forward-dns</c>, <c>docker-host</c> or
/// <c>docker-search-domain</c>.
/// </param>
/// <param name="Verified">
/// Whether the address is confirmed. For <c>browser</c> that is a certainty — a page was served over it
/// — and for the rest it means forward and reverse resolution agree. A suggestion is worth offering
/// without it, since a name only the operator's own machine resolves is still the name they type, but it
/// is the difference between "this is how you get here" and "this might be".
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
/// click instead of typing them into the LAN names setting of ADR-0033 decision 6.
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
/// The browser's own address is a candidate here rather than a chip the client makes for itself, and
/// that placement is the point: every rule that decides what may be offered — the exclusions, the
/// setting's own parser, the deduplication — lives in one place and applies to every source. A client
/// that synthesised its own chip would be a second, weaker copy of those rules, and the one it would get
/// wrong is the address a browser can legally hold and a certificate cannot name.
/// </para>
/// <para>
/// Every source is fail-open in the strong sense: an exception, a timeout, a daemon that is not there,
/// a resolver that answers nothing — each means that source contributes no candidates, never an error
/// to the client. A Settings page must not grow a red banner because a convenience could not be
/// computed. The one thing that is not swallowed is the caller's own cancellation: a client that has
/// gone away gets no answer, rather than a 200 that took the full budget to say nothing.
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
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The ceiling on the whole pass. Suggestions render under a field the operator is already looking
    /// at, so the honest failure is fewer chips rather than a page that waits.
    /// </summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The <see cref="LanNameCandidate.Source"/> of the address the browser arrived on. Named because
    /// two places act on it: the sentence it carries, and the ordering that puts it first.
    /// </summary>
    internal const string BrowserSource = "browser";

    /// <summary>
    /// The container's resolver configuration, whose <c>search</c> line Docker copies from the host.
    /// Init-only so a test can point it at a file it wrote; there is no configuration path for it, and
    /// the container of a running deployment always wants the default.
    /// </summary>
    internal string ResolvConfPath { get; init; } = "/etc/resolv.conf";

    /// <param name="hint">
    /// The address the browser reached the UI with, host only — the one address known to work, and the
    /// only thing here the server cannot find out for itself. Offered first when it survives the rules
    /// every other candidate is held to, and silently absent when it does not.
    /// </param>
    /// <param name="configuredLanNames">The setting as it stands, whose entries are not offered again.</param>
    /// <exception cref="OperationCanceledException">
    /// The caller cancelled. The budget running out does not throw — that is fewer candidates, which is
    /// this call's whole failure model — but a client that has gone away is told nothing at all.
    /// </exception>
    public async Task<IReadOnlyList<LanNameCandidate>> SuggestAsync(
        string? hint, string? configuredLanNames, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var found = new List<Found>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TotalBudget);
        var pass = new Pass(ct, budget.Token);

        await AddFromHintAsync(hint, found, pass);
        ct.ThrowIfCancellationRequested();
        await AddFromDockerHostAsync(found, pass);
        ct.ThrowIfCancellationRequested();

        return Assemble(found, Configured(configuredLanNames));
    }

    // ── Source 1: the address the browser used ───────────────────────────────────────────────────

    private async Task AddFromHintAsync(string? hint, List<Found> found, Pass pass) {
        var value = Normalize(hint);
        if (value is null) return;

        // Offered as itself first — a page was served over it, which is stronger evidence than any
        // resolver's opinion. It still goes through Assemble like everything else, so a hint the
        // certificate could not name is dropped there rather than trusted here.
        found.Add(new Found(value, BrowserSource, Verified: true, "How you reached this page."));
        if (IsExcluded(value)) return;

        if (IPAddress.TryParse(value, out var parsed)) {
            var address = Unmap(parsed);
            // An address in the browser's bar, so the interesting counterpart is the name it answers to:
            // a certificate that carries only the address makes every bookmark an address.
            foreach (var name in await ReverseAsync(address, pass)) {
                var normalized = Normalize(name);
                if (normalized is null) continue;
                var forward = await ForwardAsync(normalized, pass);
                var verified = forward.Any(a => IPAddress.TryParse(a, out var back) && Unmap(back).Equals(address));
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
        foreach (var addressText in await ForwardAsync(value, pass)) {
            if (!IPAddress.TryParse(addressText, out var resolved)) continue;
            var address = Unmap(resolved);
            var names = await ReverseAsync(address, pass);
            var verified = names.Any(n => string.Equals(Normalize(n), value, StringComparison.OrdinalIgnoreCase));
            found.Add(new Found(
                address.ToString(), "forward-dns", verified,
                verified
                    ? $"{value} resolves to this address, and reverse DNS agrees."
                    : $"{value} resolves to this address."));
        }
    }

    // ── Source 2: the Docker host's own name ─────────────────────────────────────────────────────

    private async Task AddFromDockerHostAsync(List<Found> found, Pass pass) {
        var host = Normalize(await DockerHostNameAsync(pass));
        if (host is null || IsExcluded(host) || IPAddress.TryParse(host, out _)) return;

        // Offered whether or not it resolves from in here. A container's resolver is Docker's embedded
        // one, which knows nothing about the host's name, while the operator's laptop may well resolve it
        // over mDNS or from the router's lease table — so "does not resolve here" is weak evidence
        // against a name the host itself is called.
        var hostResolves = (await ForwardAsync(host, pass)).Count > 0;
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
            if ((await ForwardAsync(fqdn, pass)).Count == 0) continue;
            found.Add(new Found(
                fqdn, "docker-search-domain", true,
                $"The Docker host's name in the {domain} search domain, and it resolves."));
        }
    }

    private async Task<string?> DockerHostNameAsync(Pass pass) {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(pass.Budget);
        lookup.CancelAfter(LookupTimeout);
        try {
            return (await docker.GetEngineInfoAsync(lookup.Token)).Name;
        } catch (OperationCanceledException) when (pass.Caller.IsCancellationRequested) {
            throw;
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
    /// rules and against the setting as it stands, deduplicated, and ordered.
    /// </summary>
    internal static IReadOnlyList<LanNameCandidate> Assemble(
        IEnumerable<Found> found, IReadOnlySet<string> configured) {
        var byValue = new Dictionary<string, LanNameCandidate>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var candidate in found) {
            // The parser the setting itself is validated by, run over the one value: a suggestion it
            // would refuse is a chip whose only effect is a Save that fails. This is also what stops a
            // browser-legal host name the certificate cannot carry — `my_nas` — from reaching a chip.
            if (!InternalCaNames.TryParseLanNames(candidate.Value, out var names, out var ips, out _)) continue;
            var (value, kind) = (names.Count, ips.Count) switch {
                (1, 0) => (names[0], "hostname"),
                (0, 1) => (Unmap(ips[0]).ToString(), "ip"),
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

        // The browser's own address first whatever else is true of it — it is the one the operator is
        // looking at — then verified, then the rest, each in discovery order. A stable ordering, so a
        // refetch does not reshuffle chips under the pointer.
        var candidates = order.Select(v => byValue[v]).ToList();
        return [
            .. candidates.Where(c => c.Source == BrowserSource),
            .. candidates.Where(c => c.Source != BrowserSource && c.Verified),
            .. candidates.Where(c => c.Source != BrowserSource && !c.Verified),
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
            foreach (var ip in ips) configured.Add(Unmap(ip).ToString());
        }
        foreach (var entry in (raw ?? "").Split(
                     [',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (Normalize(entry) is { } value) configured.Add(value);
        }
        return configured;
    }

    /// <summary>
    /// An IPv4 address written the IPv6 way, as itself. <c>::ffff:192.168.1.10</c> and
    /// <c>192.168.1.10</c> are one address, and everything here — the exclusions, the configured set,
    /// the dedupe key — compares the spelling, so the two have to arrive at the same one. Anything else
    /// is returned untouched.
    /// </summary>
    internal static IPAddress Unmap(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// Trims, drops the brackets an IPv6 authority is written in and the trailing dot of a fully
    /// qualified name, lowercases, and unmaps an IPv4-in-IPv6 address — so the same thing arriving from
    /// two sources is one string. Null when nothing is left.
    /// </summary>
    /// <remarks>
    /// Deliberately not a general "canonical spelling of this address": <c>192.168.001.010</c> means
    /// 192.168.1.8 to the framework's parser and is a typo to everybody else, so it is left exactly as
    /// written for <see cref="InternalCaNames.TryParseLanNames"/> to refuse. Only the mapped form is
    /// rewritten, because there the two spellings are the same address by definition rather than by a
    /// parsing rule nobody expects.
    /// </remarks>
    internal static string? Normalize(string? value) {
        var trimmed = (value ?? "").Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) trimmed = trimmed[1..^1];
        if (trimmed.EndsWith('.')) trimmed = trimmed[..^1];
        trimmed = trimmed.Trim().ToLowerInvariant();
        if (trimmed.Length == 0) return null;
        return IPAddress.TryParse(trimmed, out var address) && address.IsIPv4MappedToIPv6
            ? address.MapToIPv4().ToString()
            : trimmed;
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
    /// is not an address at all. Each is reachable in a browser's address bar, which is why this runs
    /// over the browser's own address too rather than trusting it.
    /// </remarks>
    internal static bool IsExcluded(string value) {
        if (IPAddress.TryParse(value, out var parsed)) {
            // Before anything else: ::ffff:127.0.0.2 is 127.0.0.2, and every check below reads the
            // address family and the octets, which the mapped form spells differently.
            var address = Unmap(parsed);
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

    /// <summary>
    /// The two tokens a pass runs under, kept apart because they mean opposite things when a lookup
    /// ends in cancellation. <paramref name="Budget"/> running out is this call's ordinary failure —
    /// fewer candidates — while <paramref name="Caller"/> being cancelled means nobody is waiting for
    /// an answer, and swallowing that would spend the whole budget producing a 200 for a closed socket.
    /// </summary>
    private readonly record struct Pass(CancellationToken Caller, CancellationToken Budget);

    private async Task<IReadOnlyList<string>> ForwardAsync(string host, Pass pass) {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(pass.Budget);
        lookup.CancelAfter(LookupTimeout);
        try {
            return await dns.ResolveAsync(host, lookup.Token);
        } catch (OperationCanceledException) when (pass.Caller.IsCancellationRequested) {
            throw;
        } catch (Exception e) {
            logger.LogDebug(e, "LAN name suggestions: {Host} could not be resolved.", host);
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> ReverseAsync(IPAddress address, Pass pass) {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(pass.Budget);
        lookup.CancelAfter(LookupTimeout);
        try {
            return await dns.ResolveNamesAsync(address, lookup.Token);
        } catch (OperationCanceledException) when (pass.Caller.IsCancellationRequested) {
            throw;
        } catch (Exception e) {
            logger.LogDebug(e, "LAN name suggestions: {Address} has no reverse name here.", address);
            return [];
        }
    }
}
