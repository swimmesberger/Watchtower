using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Watchtower.Application.Persistence;
// The entity, aliased: `InternalCa` is this namespace's own name, and the row is a different thing that
// happens to want the same word.
using InternalCaRow = Watchtower.Application.Entities.InternalCa;

namespace Watchtower.Application.Services.InternalCa;

/// <summary>
/// The root certificate and signing key held in memory for as long as a caller uses them. Owns the key,
/// so it is disposed with the caller's <c>using</c>.
/// </summary>
/// <param name="Certificate">The root, with its private key attached — what a leaf is signed against.</param>
/// <param name="CertificatePem">The root as the operator downloads it.</param>
public sealed record InternalCaRoot(X509Certificate2 Certificate, string CertificatePem) : IDisposable {
    /// <summary>The root's SHA-1 thumbprint — what a leaf's issuer is compared against.</summary>
    public string Thumbprint => Certificate.Thumbprint;

    public void Dispose() => Certificate.Dispose();
}

/// <summary>
/// The <c>internal_cas</c> table: the self-signed root Watchtower signs LAN certificates with, created
/// on first use and never rotated.
/// </summary>
/// <remarks>
/// Creation is deliberately not read-then-write, for the same reason the ACME account's is not: two
/// instances starting together would both find nothing, both generate a P-256 pair, and the deployment
/// would end up with two roots — of which an operator can only have imported one, so half the leaves
/// would be untrusted on every client. The insert is therefore unconditional and lets the unique index
/// decide, after which both instances re-read and issue from the same root.
/// <para>
/// No rotation in v1. A new root is only useful once every client has imported it, which is a manual
/// step on every device; performing it automatically would take a working LAN offline. Replacing the
/// row by hand is the escape hatch, and the next issuance follows it (the leaf is reissued whenever its
/// issuer no longer matches).
/// </para>
/// </remarks>
public sealed class InternalCaStore(
    IServiceScopeFactory scopeFactory,
    KeyProtector protector,
    TimeProvider time,
    ILogger<InternalCaStore> logger) {
    /// <summary>The subject of the root, and what the Settings page shows.</summary>
    internal const string Subject = "CN=Watchtower Internal CA";

    /// <summary>
    /// How long the root is valid. Long because replacing it costs a manual import on every client that
    /// trusts it — an expiry an operator has to notice and act on is exactly what this should not have.
    /// </summary>
    private static readonly TimeSpan RootLifetime = TimeSpan.FromDays(365 * 10);

    /// <summary>
    /// How far the root is backdated. Clocks on a LAN disagree, and a root a client considers not yet
    /// valid fails every handshake under it with an error that names the leaf rather than the cause.
    /// </summary>
    private static readonly TimeSpan Backdate = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Loads the CA, creating it on first use.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The stored key could not be read — a wrong or missing key-protection secret, or an altered row.
    /// Fatal rather than replaced: minting a new root abandons the one every client on the LAN has been
    /// told to trust, and the failure would show up on those clients rather than here.
    /// </exception>
    public async Task<InternalCaRoot> LoadOrCreateAsync(CancellationToken ct = default) {
        var existing = await ReadAsync(ct);
        if (existing is null) {
            await TryCreateAsync(ct);
            // Re-read unconditionally: this instance may have lost the insert race, and the winner's
            // root is the one the leaves have to chain to.
            existing = await ReadAsync(ct)
                       ?? throw new InvalidOperationException(
                           "The internal CA was created but could not be read back.");
        }

        var pem = protector.UnprotectText(existing.PrivateKey, existing.Protection, InternalCaNames.KeyPurpose);
        using var key = ECDsa.Create();
        try {
            key.ImportFromPem(pem);
        } catch (Exception ex) when (ex is ArgumentException or CryptographicException) {
            // Re-thrown as a CryptographicException because ImportFromPem reports a malformed PEM as an
            // ArgumentException about its parameter, which describes the call rather than the problem.
            throw new CryptographicException(
                "The stored internal CA key could not be read. Restore the key-protection secret it was "
                + "written with, or delete the internal_cas row to generate a new CA — every client that "
                + "trusts the old root then has to import the new one.",
                ex);
        }

        using var certificate = X509Certificate2.CreateFromPem(existing.CertificatePem);
        var withKey = certificate.CopyWithPrivateKey(key);
        try {
            await ReprotectAsync(existing, pem, ct);
            return new InternalCaRoot(withKey, existing.CertificatePem);
        } catch {
            withKey.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encrypts a row that was written before a key-protection secret existed. On load rather than by a
    /// migration pass, because this is the only moment the plaintext is in hand anyway — and because an
    /// operator who adopts the secret expects the keys to become encrypted without a separate step.
    /// </summary>
    private async Task ReprotectAsync(InternalCaRow row, string pem, CancellationToken ct) {
        if (!protector.IsEncrypting || row.Protection != KeyProtector.None) return;
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var updated = await db.InternalCas
                // Guarded on the row still being unprotected, so two instances starting together do not
                // both rewrite it — and so this cannot overwrite a row somebody re-encrypted under a
                // different secret in between.
                .Where(c => c.Name == row.Name && c.Protection == KeyProtector.None)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(c => c.PrivateKey, protector.ProtectText(pem, InternalCaNames.KeyPurpose))
                        .SetProperty(c => c.Protection, protector.CurrentProtection),
                    ct);
            if (updated > 0) logger.LogInformation("Encrypted the stored internal CA key at rest.");
        } catch (Exception ex) {
            // Not fatal: the key was read fine and issuance works. The next start tries again.
            logger.LogWarning(ex, "Could not encrypt the stored internal CA key at rest.");
        }
    }

    private async Task<InternalCaRow?> ReadAsync(CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.InternalCas.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name == InternalCaNames.CaRowName, ct);
    }

    /// <summary>
    /// Generates and stores the root, unless another instance got there first. The unique index on
    /// <c>name</c> is the race guard — an existence check would not be one.
    /// </summary>
    private async Task TryCreateAsync(CancellationToken ct) {
        var now = time.GetUtcNow();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256);
        // A CA that may sign leaves and nothing else: path length 0 rules out an intermediate, so a key
        // that leaked could not be used to mint a second CA under this root.
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0,
                critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        // Named so a leaf's authority key identifier points at this root rather than at "whatever has
        // this subject" — which is how the reissue check tells one generated root from another.
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var notBefore = now - Backdate;
        using var certificate = request.CreateSelfSigned(notBefore, notBefore + RootLifetime);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.InternalCas.Add(new InternalCaRow {
            Name = InternalCaNames.CaRowName,
            CertificatePem = certificate.ExportCertificatePem(),
            PrivateKey = protector.ProtectText(key.ExportPkcs8PrivateKeyPem(), InternalCaNames.KeyPurpose),
            Protection = protector.CurrentProtection,
            Subject = certificate.Subject,
            Thumbprint = certificate.Thumbprint,
            NotBefore = certificate.NotBefore.ToUniversalTime(),
            NotAfter = certificate.NotAfter.ToUniversalTime(),
            CreatedAt = now,
        });

        try {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Generated the internal CA ({Thumbprint}), valid until {NotAfter:u}. Import it on every "
                + "client that should trust Watchtower's LAN certificates.",
                certificate.Thumbprint, certificate.NotAfter.ToUniversalTime());
        } catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) {
            logger.LogInformation("Another instance created the internal CA first; using theirs.");
        }
    }
}
