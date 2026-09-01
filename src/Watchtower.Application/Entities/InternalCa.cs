namespace Watchtower.Application.Entities;

/// <summary>
/// The self-signed root Watchtower issues LAN certificates from, and the key it signs them with — one
/// row, named <c>default</c>.
/// </summary>
/// <remarks>
/// Its own table rather than a row in <c>proxy_certificates</c>: those rows are servable SNI material
/// and are pruned once nothing routes to them, and a signing key is neither servable nor something a
/// housekeeping pass may delete.
/// <para>
/// The key is the thing worth not losing. An operator imports the root into every client's trust store
/// by hand, so replacing it silently would break every device that trusted the old one — creation is
/// therefore an unconditional insert followed by a re-read, and the unique index on <see cref="Name"/>
/// decides which of two instances starting together minted it.
/// </para>
/// <para>
/// <see cref="Name"/> and <see cref="RetiredAt"/> exist for a rotation this version does not perform:
/// a second row under a different name, retired once nothing is issued from the first, is the shape a
/// rollover would take, and leaving room for it costs two columns.
/// </para>
/// </remarks>
public sealed class InternalCa : IHasXmin {
    public int Id { get; set; }

    /// <summary>Which CA this is — <c>default</c>, the only one v1 creates. Unique.</summary>
    public required string Name { get; set; }

    /// <summary>The root certificate, PEM-encoded. What the operator downloads and imports.</summary>
    public required string CertificatePem { get; set; }

    /// <summary>
    /// The root's PKCS#8 private key, protected per <see cref="Protection"/>. Bytes rather than text
    /// because AES-GCM output is not text; the unprotected form is UTF-8 PEM.
    /// </summary>
    public required byte[] PrivateKey { get; set; }

    /// <summary>How <see cref="PrivateKey"/> is encoded — see <c>KeyProtector</c>.</summary>
    public required string Protection { get; set; }

    /// <summary>The root's subject distinguished name, as the Settings page shows it.</summary>
    public required string Subject { get; set; }

    /// <summary>
    /// The root's SHA-1 thumbprint — what an operator compares against the certificate they imported,
    /// and what the leaf's issuer is checked against before it is reissued.
    /// </summary>
    public required string Thumbprint { get; set; }

    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this CA stopped being the one leaves are issued from, or null while it is current. Never
    /// set in v1: there is no rotation yet.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <inheritdoc />
    /// <remarks>Mapped by <c>XminConcurrency.UseXminAsConcurrencyToken</c>; see <see cref="IHasXmin"/>.</remarks>
    public uint Xmin { get; private set; }
}
