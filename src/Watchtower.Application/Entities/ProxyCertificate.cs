namespace Watchtower.Application.Entities;

/// <summary>
/// Where a certificate came from: an ACME order this deployment placed, one Watchtower's own internal
/// CA signed, or a PEM pair carried in from the pre-ADR-0024 <c>/data/proxy-certs</c> volume by the
/// one-shot file import.
/// </summary>
public static class ProxyCertificateSources {
    /// <summary>Issued by the CA through <c>CertificateIssuer</c>.</summary>
    public const string Acme = "acme";

    /// <summary>Imported once from the legacy certificate directory.</summary>
    public const string FileImport = "file-import";

    /// <summary>
    /// Signed by Watchtower's own internal CA for the LAN names an operator configured — no ACME, no
    /// public domain. Never part of the ACME desired set, which is why the prune skips it.
    /// </summary>
    public const string Internal = "internal-ca";
}

/// <summary>
/// One host's TLS material — ADR-0024 decision 4. The row is authoritative and the in-memory SNI map is
/// a cache of it, so any instance can serve any routed host without a shared volume.
/// </summary>
/// <remarks>
/// Leaf and intermediates together in <see cref="CertificatePem"/>, exactly as the CA issued them: the
/// shipped container has no reason to hold Let's Encrypt's intermediates, so a chain reassembled from a
/// trust store would be missing the very certificates the handshake has to send.
/// <para>
/// The validity, issuer and thumbprint columns are derived from the leaf. They are stored anyway because
/// they are what the Routes page and the certificates list read, and parsing every PEM to answer "when
/// does this expire" would make a list query an X.509 exercise.
/// </para>
/// </remarks>
public sealed class ProxyCertificate : IHasXmin {
    public int Id { get; set; }

    /// <summary>The SNI name this certificate answers, lowercased. Unique.</summary>
    public required string Host { get; set; }

    /// <summary>The issued chain, leaf first, as concatenated PEM blocks.</summary>
    public required string CertificatePem { get; set; }

    /// <summary>
    /// The leaf's PKCS#8 private key, protected per <see cref="Protection"/>. Bytes rather than text
    /// because AES-GCM output is not text; the unprotected form is UTF-8 PEM.
    /// </summary>
    public required byte[] PrivateKey { get; set; }

    /// <summary>How <see cref="PrivateKey"/> is encoded — see <c>KeyProtector</c>.</summary>
    public required string Protection { get; set; }

    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }

    /// <summary>The issuer's common name, as the certificates list shows it.</summary>
    public required string Issuer { get; set; }

    /// <summary>The leaf's SHA-1 thumbprint — what the route-status projection suppresses rewrites on.</summary>
    public required string Thumbprint { get; set; }

    /// <summary>One of <see cref="ProxyCertificateSources"/>.</summary>
    public required string Source { get; set; }

    public DateTimeOffset InstalledAt { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Mapped by <c>XminConcurrency.UseXminAsConcurrencyToken</c>; see <see cref="IHasXmin"/> for why
    /// this is a real property rather than an EF shadow property. Last, because it is the database's
    /// bookkeeping rather than part of what this entity means.
    /// </remarks>
    public uint Xmin { get; private set; }
}
