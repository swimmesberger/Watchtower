using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Watchtower.Application.Services.InternalCa;

/// <summary>
/// One issued LAN leaf: the certificate, the key it was issued for, and the PEM the certificate store
/// takes. Owns the key, so it is disposed with the caller's <c>using</c>.
/// </summary>
public sealed record InternalLeaf(X509Certificate2 Certificate, ECDsa Key, string PemChain) : IDisposable {
    public void Dispose() {
        Certificate.Dispose();
        Key.Dispose();
    }
}

/// <summary>
/// Signs a server certificate for the configured LAN names against Watchtower's own root. Pure: no
/// database, no clock of its own, no store — everything about <em>when</em> a leaf is issued belongs to
/// <see cref="InternalCertificateService"/>, and everything about what one contains is here and testable
/// without either.
/// </summary>
public static class InternalCaIssuer {
    /// <summary>
    /// TLS server authentication. Not decoration: Kestrel's <c>EnsureCertificateIsAllowedForServerAuth</c>
    /// refuses a certificate that carries an extended key usage without this OID in it, so a leaf issued
    /// without it would be rejected before any client ever saw it.
    /// </summary>
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// How long a leaf is valid. A year, and renewed at two thirds of that by the shared renewal policy
    /// — short enough to be a real key rotation, long enough that nothing on the LAN depends on
    /// Watchtower having been up in the last few weeks.
    /// </summary>
    private static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(365);

    /// <summary>
    /// How far a leaf is backdated, for the same clock-skew reason as the root's.
    /// </summary>
    private static readonly TimeSpan Backdate = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Issues one leaf covering every configured name, against <paramref name="root"/>'s key.
    /// </summary>
    /// <remarks>
    /// One certificate for all of them rather than one per name: a client reaching a bare IP sends no
    /// usable SNI, so the listener cannot pick between certificates anyway — what it can do is present
    /// one that names every address the operator said this deployment answers on.
    /// </remarks>
    /// <exception cref="ArgumentException">Neither a DNS name nor an IP address was given.</exception>
    /// <exception cref="InvalidOperationException">
    /// The root has expired, so nothing it signs could be valid. Stated rather than left to the platform,
    /// which reports it as two dates in the wrong order.
    /// </exception>
    public static InternalLeaf IssueLeaf(
        X509Certificate2 root,
        IReadOnlyList<string> dnsNames,
        IReadOnlyList<IPAddress> ips,
        DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(dnsNames);
        ArgumentNullException.ThrowIfNull(ips);
        if (dnsNames.Count == 0 && ips.Count == 0)
            throw new ArgumentException(
                "A LAN certificate needs at least one host name or IP address.", nameof(dnsNames));

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try {
            var request = new CertificateRequest(
                new X500DistinguishedName($"CN={InternalCaNames.SharedLeafHost}"), key,
                HashAlgorithmName.SHA256);

            var sans = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames) sans.AddDnsName(name);
            // A separate SAN kind, not a host name that happens to look numeric: a browser asked for a
            // bare address matches only this one.
            foreach (var ip in ips) sans.AddIpAddress(ip);
            request.CertificateExtensions.Add(sans.Build());

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(
                    certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0,
                    critical: true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid(ServerAuthenticationOid)], critical: false));
            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
            // Names the exact root this was signed by, which is what lets a reissue notice that the CA
            // row was replaced. Key identifier only: the issuer-and-serial form adds nothing a local
            // two-certificate chain can use.
            request.CertificateExtensions.Add(
                X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                    root, includeKeyIdentifier: true, includeIssuerAndSerial: false));
            // Deliberately no AIA and no CRL distribution point. Both are URLs a client would fetch
            // mid-handshake, and there is nothing on a LAN to serve them: leaving them out keeps chain
            // building purely local, which is the only way it can work on a network with no route out.

            var notBefore = now - Backdate;
            // Clamped to the root, because CertificateRequest.Create throws outright for a leaf that
            // outlives its issuer. Ten years in, that is what an unclamped year would be — and the throw
            // would come out of the renewal pass, putting every port route into Error with an exception
            // about a date. A short final leaf is the honest answer: it expires with the anchor, which is
            // the moment the operator has to replace the CA row and re-import it anyway.
            var expiry = notBefore + LeafLifetime;
            var ceiling = new DateTimeOffset(root.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            // Past the anchor's own expiry the clamp has nothing left to clamp to, and Create would throw
            // an ArgumentException about the order of two dates — true, and useless to the person who has
            // to act on it. Said plainly instead: this surfaces through EnsureAsync's catch onto the port
            // routes' rows, so it is what an operator reads on the Routes page.
            if (ceiling <= notBefore)
                throw new InvalidOperationException(
                    $"The internal CA expired on {root.NotAfter.ToUniversalTime():u}, so it can no longer "
                    + "sign a LAN certificate. Delete the internal_cas row so a new root is minted, then "
                    + "re-import it on every device that trusts the old one.");
            var certificate = request.Create(
                root, notBefore, expiry < ceiling ? expiry : ceiling, RandomNumberGenerator.GetBytes(16));
            try {
                // The leaf alone. There is no intermediate to send — the chain is two certificates deep
                // — and the root is the one thing the client already has, by construction.
                return new InternalLeaf(certificate, key, certificate.ExportCertificatePem() + "\n");
            } catch {
                certificate.Dispose();
                throw;
            }
        } catch {
            key.Dispose();
            throw;
        }
    }
}
