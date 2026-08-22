using System.Net;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// Resolves a host's A/AAAA records — the one question asked before any ACME traffic is generated, and
/// the same one <c>proxy.checkDns</c> answers for the Routes page. ADR-0020.
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
}
