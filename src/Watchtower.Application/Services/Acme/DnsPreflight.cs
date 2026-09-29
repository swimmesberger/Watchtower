using System.Net;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// Resolves a host's A/AAAA records — the one question asked before any ACME traffic is generated, and
/// the same one <c>proxy.checkDns</c> answers for the Routes page. ADR-0022.
/// </summary>
/// <remarks>
/// A class rather than a static call so it can be substituted in tests, and shared between the handler
/// and the issuer so the operator's "check DNS" button and the certificate machinery cannot disagree
/// about whether a domain points here.
/// <para>
/// Never throws. Every failure mode — NXDOMAIN, a timeout, no resolver configured — means the same thing
/// to both callers: not yet. Distinguishing them would produce error text about the resolver where the
/// operator needs text about their DNS record.
/// </para>
/// </remarks>
public class DnsPreflight {
    /// <summary>
    /// The addresses <paramref name="host"/> currently resolves to, or an empty list when it resolves to
    /// nothing.
    /// </summary>
    public virtual async Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(host)) return [];
        try {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            return addresses.Select(a => a.ToString()).ToArray();
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return [];
        }
    }

    /// <summary>
    /// The host names <paramref name="address"/> reverse-resolves to (its PTR records), or an empty list
    /// when it reverse-resolves to nothing.
    /// </summary>
    /// <remarks>
    /// The other direction of <see cref="ResolveAsync"/>, in the same shape and never throwing for the
    /// same reason: a LAN with no reverse zone is the ordinary case rather than a fault to report. Added
    /// for the LAN-name suggestions of ADR-0033 decision 6, which turn an address the browser reached
    /// this page with into the name that address answers to.
    /// <para>
    /// Asked through the string overload, which is the only one that takes a cancellation token. What
    /// comes back is read by <see cref="NamesFrom"/>.
    /// </para>
    /// </remarks>
    public virtual async Task<IReadOnlyList<string>> ResolveNamesAsync(IPAddress address, CancellationToken ct) {
        if (address is null) return [];
        try {
            return NamesFrom(await Dns.GetHostEntryAsync(address.ToString(), ct));
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return [];
        }
    }

    /// <summary>
    /// The host names a reverse lookup's answer actually carries: its canonical name and every alias
    /// beside it, deduplicated, in that order.
    /// </summary>
    /// <remarks>
    /// Both halves matter. <see cref="IPHostEntry.Aliases"/> is where the second and subsequent PTR
    /// records land, and an address with more than one name is ordinary rather than exotic — a box
    /// reached as <c>nas</c> and <c>nas.lan</c> is the case this exists to serve.
    /// <para>
    /// An IP-shaped answer is dropped, because with nothing to answer the resolver hands the address
    /// straight back as the host name. A caller asking what an address is called must not be told it is
    /// called 192.168.1.10 — and that is true of any address it echoes, not only the one asked about,
    /// so the filter is the shape rather than the value.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> NamesFrom(IPHostEntry? entry) {
        if (entry is null) return [];
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in (string?[])[entry.HostName, .. entry.Aliases ?? []]) {
            var name = (candidate ?? "").Trim();
            if (name.Length == 0 || IPAddress.TryParse(name, out _)) continue;
            if (seen.Add(name)) names.Add(name);
        }
        return names;
    }
}
