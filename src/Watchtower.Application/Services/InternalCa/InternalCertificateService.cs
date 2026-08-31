using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Services.InternalCa;

/// <summary>
/// Keeps the one LAN certificate the in-process proxy serves on port routes in line with the configured
/// LAN names — the internal CA's counterpart to <see cref="CertificateManager"/>.
/// </summary>
/// <remarks>
/// Idempotent and cheap: every call decides whether the held leaf still says what it should, and does
/// nothing when it does. That is what lets it be called from startup, from a route change and from the
/// renewal pass without any of the three knowing about the others.
/// <para>
/// Deliberately <em>not</em> gated on the <c>acme-issuer</c> lease. That lease protects a rate-limited
/// remote resource; issuing here is local, free and row-race-guarded, and the instance an operator is
/// talking to has to be able to make the route they just created work — not wait for whichever node
/// holds a lease that exists for a different reason.
/// </para>
/// <para>
/// Nor on <see cref="Services.Yarp.YarpListenerState.HttpsBound"/>. A deployment that serves nothing but
/// port routes runs with the HTTPS ingress port off, and gating on it would mean exactly that deployment
/// never gets a certificate.
/// </para>
/// </remarks>
public sealed class InternalCertificateService(
    CertificateStore store,
    InternalCaStore caStore,
    IOptionsMonitor<WatchtowerOptions> options,
    TimeProvider time,
    ILogger<InternalCertificateService> logger) {
    /// <summary>
    /// Issues or reissues the shared LAN leaf if anything wants one. Never throws for an issuance
    /// problem: this runs on the startup path and from background passes, and a CA that cannot be
    /// written must not take the host down with it.
    /// </summary>
    public async Task EnsureAsync(CancellationToken ct = default) {
        try {
            await EnsureCoreAsync(IsLeafWantedAsync, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogError(ex, "Could not issue the internal LAN certificate; retrying on the next pass.");
        }
    }

    /// <summary>
    /// The decision itself, with the "is a leaf wanted" question passed in — which is what lets it be
    /// exercised without the routing state the production predicate reads.
    /// </summary>
    internal async Task EnsureCoreAsync(Func<CancellationToken, ValueTask<bool>> wanted, CancellationToken ct) {
        var proxy = options.CurrentValue.Proxy;
        // The same gate the certificate manager applies: these certificates are served by the in-process
        // proxy's listeners, and under any other provider there is nothing to serve them.
        if (!proxy.Enabled || proxy.ResolveProvider() != ProxyProviderKind.Yarp) return;
        if (!await wanted(ct)) return;

        if (!InternalCaNames.TryParseLanNames(
                proxy.Yarp.LanNames, out var dnsNames, out var ips, out var reason)) {
            // Refused at the point it was typed, so this is a value that arrived through the environment.
            if (FirstTime($"unreadable:{reason}"))
                logger.LogWarning(
                    "Not issuing a LAN certificate: Proxy:Yarp:LanNames could not be read — {Reason}", reason);
            return;
        }
        if (dnsNames.Count == 0 && ips.Count == 0) {
            if (FirstTime("unconfigured"))
                logger.LogWarning(
                    "Not issuing a LAN certificate: no LAN names are configured. Set the addresses this "
                    + "deployment is reached on under Settings → Reverse proxy.");
            return;
        }
        _lastRefusal = null;

        var current = store.SelectCertificate(InternalCaNames.SharedLeafHost);
        var entry = store.Find(InternalCaNames.SharedLeafHost);
        // The public half only, and only when something is held: deciding that a certificate is still
        // fine must not cost a key decryption on every pass, and the issuer check reads a key identifier
        // rather than a key. The load-or-create below is what a reissue pays for.
        using var issuer = current is null ? null : await caStore.ReadCertificateAsync(ct);
        var why = ReissueReason(current, entry, issuer, dnsNames, ips, time.GetUtcNow());
        if (why is null) return;

        using var root = await caStore.LoadOrCreateAsync(ct);
        using var leaf = InternalCaIssuer.IssueLeaf(root.Certificate, dnsNames, ips, time.GetUtcNow());
        await store.InstallInternalAsync(InternalCaNames.SharedLeafHost, leaf.PemChain, leaf.Key, ct);
        logger.LogInformation(
            "Issued a LAN certificate for {Names} from the internal CA ({Reason}); it is valid until "
            + "{NotAfter:u}.",
            string.Join(", ", dnsNames.Concat(ips.Select(ip => ip.ToString()))), why,
            leaf.Certificate.NotAfter.ToUniversalTime());
    }

    /// <summary>
    /// Whether a refusal is worth a log line: the first time it happens, and again whenever the reason
    /// changes. This runs on a background cadence, so an operator who has not configured LAN names would
    /// otherwise get the same warning every five minutes for the life of the process.
    /// </summary>
    /// <remarks>
    /// A plain field rather than a lock: the cost of two passes racing is one duplicated warning, which
    /// is precisely the thing this is not worth synchronising for.
    /// </remarks>
    private bool FirstTime(string refusal) {
        if (string.Equals(_lastRefusal, refusal, StringComparison.Ordinal)) return false;
        _lastRefusal = refusal;
        return true;
    }

    private string? _lastRefusal;

    /// <summary>
    /// Whether anything currently needs a LAN certificate.
    /// </summary>
    /// <remarks>
    /// Nothing does yet: port routes are what want one, and they do not exist in this stage. Stage 2
    /// wires this to port routes.
    /// </remarks>
    private static ValueTask<bool> IsLeafWantedAsync(CancellationToken ct) => ValueTask.FromResult(false);

    /// <summary>
    /// Why the held leaf has to be replaced, or null when it still says everything it should. Pure, and
    /// stated as a sentence because it is what the issuance log line reports.
    /// </summary>
    /// <param name="root">
    /// The CA the leaf should have been signed by, or null when there is no CA row — which a held leaf
    /// outlives only when somebody deleted it, and is exactly the case that must not be served on.
    /// </param>
    internal static string? ReissueReason(
        X509Certificate2? current,
        CertificateEntry? entry,
        X509Certificate2? root,
        IReadOnlyList<string> dnsNames,
        IReadOnlyList<IPAddress> ips,
        DateTimeOffset now) {
        if (current is null || entry is null) return "none held";
        if (!CoversExactly(current, dnsNames, ips)) return "the LAN names changed";
        // Not "was it signed by a CA with this subject": every generated root carries the same subject,
        // so only the key identifier tells one from another — which is what a hand-replaced CA row
        // changes.
        if (root is null || !IssuedBy(current, root)) return "the internal CA changed";
        if (CertificateRenewalPolicy.IsRenewalDue(now, entry.NotBefore, entry.NotAfter)) return "renewal due";
        return null;
    }

    /// <summary>
    /// Whether the leaf names exactly the configured set — neither missing one (a device that cannot
    /// reach the service) nor carrying one the operator has removed.
    /// </summary>
    private static bool CoversExactly(
        X509Certificate2 leaf, IReadOnlyList<string> dnsNames, IReadOnlyList<IPAddress> ips) {
        if (SubjectAltNames(leaf) is not { } san) return false;
        var heldNames = new HashSet<string>(san.EnumerateDnsNames(), StringComparer.OrdinalIgnoreCase);
        // Compared as address bytes, which is the only form both sides certainly agree on: an address
        // read back out of a certificate has no scope id and no textual spelling of its own, so
        // comparing the two ToString() results would be comparing a certificate against a habit.
        var heldIps = new HashSet<string>(san.EnumerateIPAddresses().Select(Key), StringComparer.Ordinal);
        return heldNames.SetEquals(dnsNames) && heldIps.SetEquals(ips.Select(Key));

        static string Key(IPAddress ip) => Convert.ToHexString(ip.GetAddressBytes());
    }

    /// <summary>Whether the leaf's authority key identifier names <paramref name="root"/>'s key.</summary>
    private static bool IssuedBy(X509Certificate2 leaf, X509Certificate2 root) {
        if (AuthorityKeyIdentifier(leaf) is not { KeyIdentifier: { } aki }) return false;
        if (SubjectKeyIdentifier(root) is not { } ski) return false;
        return string.Equals(
            Convert.ToHexString(aki.Span), ski.SubjectKeyIdentifier, StringComparison.OrdinalIgnoreCase);
    }

    // The three extension readers below all decode from the raw extension when the platform handed back
    // a plain X509Extension rather than the typed subclass — a pattern-match filter alone would report
    // "the extension is absent" for a certificate that carries it.

    /// <summary>The names the certificate answers for, or null when it carries no SAN extension.</summary>
    private static X509SubjectAlternativeNameExtension? SubjectAltNames(X509Certificate2 certificate) =>
        certificate.Extensions[SubjectAltNameOid] switch {
            X509SubjectAlternativeNameExtension typed => typed,
            { } raw => new X509SubjectAlternativeNameExtension(raw.RawData, raw.Critical),
            _ => null,
        };

    private static X509AuthorityKeyIdentifierExtension? AuthorityKeyIdentifier(X509Certificate2 certificate) =>
        certificate.Extensions[AuthorityKeyIdentifierOid] switch {
            X509AuthorityKeyIdentifierExtension typed => typed,
            { } raw => new X509AuthorityKeyIdentifierExtension(raw.RawData, raw.Critical),
            _ => null,
        };

    private static X509SubjectKeyIdentifierExtension? SubjectKeyIdentifier(X509Certificate2 certificate) =>
        certificate.Extensions[SubjectKeyIdentifierOid] switch {
            X509SubjectKeyIdentifierExtension typed => typed,
            // The AsnEncodedData overload, not the byte[] one: that takes the identifier itself rather
            // than the encoded extension, and would read the DER header as part of the key id.
            { } raw => new X509SubjectKeyIdentifierExtension(new AsnEncodedData(raw.RawData), raw.Critical),
            _ => null,
        };

    private const string SubjectAltNameOid = "2.5.29.17";
    private const string SubjectKeyIdentifierOid = "2.5.29.14";
    private const string AuthorityKeyIdentifierOid = "2.5.29.35";
}
