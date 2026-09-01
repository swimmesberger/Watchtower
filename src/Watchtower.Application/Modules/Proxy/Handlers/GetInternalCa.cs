using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.InternalCa;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Watchtower's own certificate authority, as the Settings and Routes pages report it.
/// </summary>
/// <param name="Present">
/// Whether a CA exists at all. False until something first needs a LAN certificate — reading this page
/// deliberately does not create one, because a root that exists is a root an operator is invited to
/// import, and inviting that before anything uses it is noise.
/// </param>
/// <param name="LeafNotAfter">When the currently served LAN certificate expires, or null if none is held.</param>
/// <param name="SubjectAltNames">The names that certificate answers for — the LAN names, as issued.</param>
/// <param name="DownloadPath">Where to fetch the root from, so a client never builds the URL itself.</param>
public sealed record InternalCaDto(
    bool Present,
    string? Subject,
    string? Thumbprint,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    DateTimeOffset? LeafNotAfter,
    IReadOnlyList<string> SubjectAltNames,
    string DownloadPath);

/// <summary>
/// Reports the internal CA and the LAN certificate issued from it. Read-only in the strong sense: it
/// never creates the CA, so an operator opening the page cannot mint a root nobody asked for.
/// </summary>
[Handler("proxy.getInternalCa")]
public sealed class GetInternalCa(WatchtowerDbContext db, CertificateStore certificates)
    : IHandler<GetInternalCa.Query, Result<GetInternalCa.Response>> {
    public sealed record Query;

    public sealed record Response(InternalCaDto Ca);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var ca = await db.InternalCas.AsNoTracking()
            .Where(c => c.Name == InternalCaNames.CaRowName)
            .Select(c => new { c.Subject, c.Thumbprint, c.NotBefore, c.NotAfter })
            .FirstOrDefaultAsync(ct);

        // From the store rather than from a second row read: what the page should report is what this
        // instance is actually serving, which is the same thing a handshake would get.
        var leaf = certificates.SelectCertificate(InternalCaNames.SharedLeafHost);

        return new Response(new InternalCaDto(
            Present: ca is not null,
            Subject: ca?.Subject,
            Thumbprint: ca?.Thumbprint,
            NotBefore: ca?.NotBefore,
            NotAfter: ca?.NotAfter,
            LeafNotAfter: leaf?.NotAfter.ToUniversalTime(),
            SubjectAltNames: leaf is null ? [] : SubjectAltNames(leaf),
            DownloadPath: InternalCaNames.DownloadPath));
    }

    /// <summary>
    /// The DNS and IP entries of a leaf's subject alternative name extension, in one flat list — which
    /// is how they are shown and how an operator compares them against what they typed.
    /// </summary>
    private static IReadOnlyList<string> SubjectAltNames(X509Certificate2 leaf) {
        // Decoded from the raw extension when the platform handed back an untyped one; a filter that
        // only accepted the typed form would report a certificate's names as absent.
        var san = leaf.Extensions[SubjectAltNameOid] switch {
            X509SubjectAlternativeNameExtension typed => typed,
            { } raw => new X509SubjectAlternativeNameExtension(raw.RawData, raw.Critical),
            _ => null,
        };
        if (san is null) return [];
        return [.. san.EnumerateDnsNames(), .. san.EnumerateIPAddresses().Select(ip => ip.ToString())];
    }

    private const string SubjectAltNameOid = "2.5.29.17";
}
