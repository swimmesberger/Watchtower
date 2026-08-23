using Elarion.Settings;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Security.Cryptography;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
// See AcmeAccountKey.cs: the row and the CA's account resource share a name.
using AcmeAccountRow = Watchtower.Application.Entities.AcmeAccount;

namespace Watchtower.Application.Services;

/// <summary>
/// Carries a pre-ADR-0024 installation's key and certificate <em>files</em> into the database, once, on
/// the first start after the upgrade.
/// </summary>
/// <remarks>
/// <para>
/// The alternative was to make operators do it, and the reason not to is the data-protection key ring:
/// without it every logged-in session — and every OIDC correlation cookie mid-login — is invalidated by
/// the upgrade, which looks to users like an outage and to an operator like a bug. The ACME account key
/// matters for the same kind of reason (an account is rate-limited per key, and a fresh one throws away
/// the deployment's issuance history), and the certificates simply avoid re-ordering everything against
/// Let's Encrypt's limits on the day of an upgrade.
/// </para>
/// <para>
/// Idempotent twice over: a sentinel setting records that it ran, and every individual import is
/// conditional on the target being empty. That second guard is the one that matters, because the
/// sentinel is a setting an operator could clear — an import that ran again must not overwrite a
/// certificate issued since, or resurrect a key ring somebody deliberately rotated.
/// </para>
/// <para>
/// Nothing is ever deleted. The files stay exactly where they were, so an operator who has to roll the
/// image back still has a working installation, and removing them is a step they take when they are
/// satisfied (docs/upgrading.md).
/// </para>
/// </remarks>
public sealed class FileStateImport(
    WatchtowerDbContext db,
    CertificateStore certificates,
    KeyProtector protector,
    ISettingsManager settings,
    IConfiguration configuration,
    AuditLog audit,
    TimeProvider time,
    ILogger<FileStateImport> logger) {
    /// <summary>Where the identity-assertion key and the data-protection key ring used to live.</summary>
    public const string DefaultKeyPath = "/data/auth-keys";

    /// <summary>Where the issued certificates and the ACME account material used to live.</summary>
    public const string DefaultCertPath = "/data/proxy-certs";

    /// <summary>
    /// The removed configuration paths, still read for the one upgrade that needs them — so a deployment
    /// that moved its volume with <c>WATCHTOWER__AUTH__KEYPATH</c> or
    /// <c>WATCHTOWER__PROXY__YARP__CERTPATH</c> is imported from where its files actually are, rather
    /// than having the import silently find nothing under the defaults.
    /// </summary>
    private const string KeyPathSetting = "Watchtower:Auth:KeyPath";
    private const string CertPathSetting = "Watchtower:Proxy:Yarp:CertPath";

    /// <summary>
    /// The advisory lock two instances upgrading together serialize on. An arbitrary constant, chosen
    /// once and never reused; PostgreSQL's advisory locks share one namespace per database.
    /// </summary>
    private const long ImportLockKey = 4919002;

    /// <summary>Runs the import if it has not run before. Never throws — see the class remarks.</summary>
    public async Task RunAsync(CancellationToken ct = default) {
        try {
            // Two instances started together would otherwise both find an empty database and both walk
            // the same directories. The individual guards make most of that harmless, but the key ring
            // is the exception: both would see an empty table and both would insert the whole ring, and
            // a duplicated ring is a set of keys with two rows each. Serialising the import is cheaper
            // than making four different imports individually atomic.
            //
            // pg_advisory_xact_lock rather than the session form: it is released by the commit below on
            // every path out, including the throwing one, with nothing to remember to unlock. The lock
            // is taken before the sentinel is read, so the instance that waits sees the winner's write
            // rather than the state it started from.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ImportLockKey})", ct);

            if (await settings.GetStringAsync(
                    WatchtowerSettingPaths.AuthFileStateImported, SettingsScope.Global, ct) is not null) {
                await transaction.CommitAsync(ct);
                return;
            }

            var keyPath = Resolve(KeyPathSetting, DefaultKeyPath);
            var certPath = Resolve(CertPathSetting, DefaultCertPath);
            var summary = new ImportSummary();

            if (Directory.Exists(keyPath)) {
                await ImportSigningKeyAsync(keyPath, summary, ct);
                await ImportDataProtectionKeysAsync(keyPath, summary, ct);
            }
            if (Directory.Exists(certPath)) {
                await ImportAcmeAccountsAsync(certPath, summary, ct);
                await ImportCertificatesAsync(certPath, summary, ct);
            }

            // The sentinel is written whatever was found, including nothing: a fresh install has no files
            // and should not walk two directories on every restart for the rest of its life.
            await settings.SetStringAsync(
                WatchtowerSettingPaths.AuthFileStateImported,
                time.GetUtcNow().ToString("O"),
                SettingsScope.Global,
                expectedVersion: null,
                ct);

            await transaction.CommitAsync(ct);

            if (summary.IsEmpty) {
                logger.LogDebug("No legacy key or certificate files to import.");
                return;
            }

            logger.LogInformation(
                "Imported legacy state into the database: {SigningKeys} signing key(s), {RingKeys} "
                + "data-protection key(s), {Accounts} ACME account(s), {Certificates} certificate(s). The "
                + "files under {KeyPath} and {CertPath} are no longer read and can be removed.",
                summary.SigningKeys, summary.DataProtectionKeys, summary.AcmeAccounts,
                summary.Certificates, keyPath, certPath);
            await audit.RecordAsync(
                CertificateIssuer.AuditCategory, "state.import", "legacy files",
                $"{summary.SigningKeys} signing key(s), {summary.DataProtectionKeys} data-protection "
                + $"key(s), {summary.AcmeAccounts} ACME account(s), {summary.Certificates} certificate(s)",
                ct: ct);
        } catch (Exception ex) {
            // A failed import must not stop the host: everything it carries across is a convenience, and
            // the deployment works without it (a new signing key, new sessions, re-ordered certificates).
            // The sentinel is not written on this path, so the next start tries again.
            logger.LogError(
                ex, "Importing the legacy key and certificate files failed; the deployment starts with "
                    + "fresh state. Sessions will have been signed out and certificates will be re-ordered.");
        }
    }

    /// <summary>
    /// The directory to read: whatever the deployment still configures for it, otherwise the default the
    /// shipped image used. Straight off <see cref="IConfiguration"/> rather than the options record,
    /// because the two settings are gone from the model — this is the only code left that knows they
    /// ever existed.
    /// </summary>
    private string Resolve(string path, string fallback) {
        var configured = configuration[path];
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }

    // ── The identity-assertion signing key ────────────────────────────────────

    private async Task ImportSigningKeyAsync(string keyPath, ImportSummary summary, CancellationToken ct) {
        var path = Path.Combine(keyPath, AuthTokenSigner.KeyFileName);
        if (!File.Exists(path)) return;
        if (await db.SigningKeys.AnyAsync(k => k.Purpose == SigningKey.IdentityAssertionPurpose, ct)) return;

        using var key = ECDsa.Create();
        try {
            key.ImportFromPem(await File.ReadAllTextAsync(path, ct));
        } catch (Exception ex) when (ex is ArgumentException or CryptographicException) {
            logger.LogWarning(
                ex, "Not importing {Path}: it is not a readable PEM private key. A new identity-assertion "
                    + "key will be generated, so apps that cached the JWKS have to refetch it.", path);
            return;
        }

        db.SigningKeys.Add(new SigningKey {
            Purpose = SigningKey.IdentityAssertionPurpose,
            PrivateKey = protector.ProtectText(key.ExportPkcs8PrivateKeyPem(), AuthTokenSigner.KeyPurpose),
            Protection = protector.CurrentProtection,
            KeyId = SigningKeyId(key),
            CreatedAt = time.GetUtcNow(),
        });
        await SaveIgnoringRaceAsync(ct);
        summary.SigningKeys++;
    }

    /// <summary>
    /// The <c>kid</c>, computed the same way the signer computes it. Duplicated as one line rather than
    /// exposing the signer's private helper, because the alternative is a public API whose only caller is
    /// a one-shot upgrade step.
    /// </summary>
    private static string SigningKeyId(ECDsa key) {
        using var publicOnly = ECDsa.Create();
        publicOnly.ImportParameters(key.ExportParameters(includePrivateParameters: false));
        var jwk = Microsoft.IdentityModel.Tokens.JsonWebKeyConverter.ConvertFromECDsaSecurityKey(
            new Microsoft.IdentityModel.Tokens.ECDsaSecurityKey(publicOnly));
        return Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
    }

    // ── The ASP.NET data-protection key ring ──────────────────────────────────

    /// <summary>
    /// Copies the XML key files ASP.NET wrote into the <c>data_protection_keys</c> table. This is the
    /// import that keeps people signed in across the upgrade: the session cookie is not itself
    /// data-protected, but the password-reset tokens and the OIDC correlation/nonce cookies are, and the
    /// ring is what the whole mechanism is keyed on.
    /// </summary>
    /// <remarks>
    /// All or nothing, on the table being empty. Merging file keys into a ring that already has rows
    /// would be the one situation where "import twice" is genuinely wrong: the newest key wins for
    /// <em>writing</em>, and reviving an older one from a file could hand new payloads to a key an
    /// operator retired on purpose.
    /// </remarks>
    private async Task ImportDataProtectionKeysAsync(
        string keyPath, ImportSummary summary, CancellationToken ct) {
        if (await db.DataProtectionKeys.AnyAsync(ct)) return;

        string[] files;
        try {
            files = Directory.GetFiles(keyPath, "key-*.xml");
        } catch (Exception ex) {
            logger.LogWarning(ex, "Could not list the data-protection keys under {KeyPath}.", keyPath);
            return;
        }
        if (files.Length == 0) return;

        foreach (var file in files) {
            string xml;
            try {
                xml = await File.ReadAllTextAsync(file, ct);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Skipping the data-protection key {File}.", file);
                continue;
            }
            db.DataProtectionKeys.Add(new DataProtectionKey {
                // ASP.NET's own repository uses the file name stem as the friendly name, so a ring
                // imported this way is indistinguishable from one it wrote itself.
                FriendlyName = Path.GetFileNameWithoutExtension(file),
                Xml = xml,
            });
            summary.DataProtectionKeys++;
        }
        await SaveIgnoringRaceAsync(ct);
    }

    // ── The ACME account ──────────────────────────────────────────────────────

    /// <summary>
    /// Copies <c>accounts/{hash}/account.key</c> plus its <c>account.json</c>. The directory name was a
    /// hash of the directory URL, so the URL itself is read out of the JSON rather than reconstructed.
    /// </summary>
    private async Task ImportAcmeAccountsAsync(
        string certPath, ImportSummary summary, CancellationToken ct) {
        var accountsRoot = Path.Combine(certPath, "accounts");
        if (!Directory.Exists(accountsRoot)) return;

        foreach (var directory in Directory.GetDirectories(accountsRoot)) {
            var keyFile = Path.Combine(directory, "account.key");
            var accountFile = Path.Combine(directory, "account.json");
            if (!File.Exists(keyFile) || !File.Exists(accountFile)) continue;

            AcmeAccountFile? stored;
            try {
                stored = System.Text.Json.JsonSerializer.Deserialize(
                    await File.ReadAllTextAsync(accountFile, ct), AcmeJsonContext.Default.AcmeAccountFile);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Skipping the ACME account in {Directory}: {File} is unreadable.",
                    directory, accountFile);
                continue;
            }
            if (string.IsNullOrWhiteSpace(stored?.DirectoryUrl)) continue;
            if (await db.AcmeAccounts.AnyAsync(a => a.DirectoryUrl == stored.DirectoryUrl, ct)) continue;

            using var key = ECDsa.Create();
            try {
                key.ImportFromPem(await File.ReadAllTextAsync(keyFile, ct));
            } catch (Exception ex) when (ex is ArgumentException or CryptographicException) {
                logger.LogWarning(
                    ex, "Skipping the ACME account key {File}: it is not a readable PEM private key.",
                    keyFile);
                continue;
            }

            db.AcmeAccounts.Add(new AcmeAccountRow {
                DirectoryUrl = stored.DirectoryUrl,
                PrivateKey = protector.ProtectText(
                    key.ExportPkcs8PrivateKeyPem(), AcmeAccountStore.KeyPurpose),
                Protection = protector.CurrentProtection,
                AccountUrl = string.IsNullOrWhiteSpace(stored.AccountUrl) ? null : stored.AccountUrl,
                CreatedAt = time.GetUtcNow(),
            });
            await SaveIgnoringRaceAsync(ct);
            summary.AcmeAccounts++;
        }
    }

    // ── The certificates ──────────────────────────────────────────────────────

    /// <summary>
    /// Copies every <c>{host}/cert.pem</c> + <c>key.pem</c> pair. Goes through
    /// <see cref="CertificateStore.ImportAsync"/> rather than writing rows here, so the parsing, the
    /// validity/issuer/thumbprint derivation and the host validation are the same code that an issuance
    /// runs — an imported certificate that the store would refuse to serve is one that never reaches the
    /// table.
    /// </summary>
    private async Task ImportCertificatesAsync(
        string certPath, ImportSummary summary, CancellationToken ct) {
        foreach (var directory in Directory.GetDirectories(certPath)) {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "accounts", StringComparison.Ordinal)) continue;

            var certFile = Path.Combine(directory, "cert.pem");
            var keyFile = Path.Combine(directory, "key.pem");
            if (!File.Exists(certFile) || !File.Exists(keyFile)) continue;

            try {
                if (await certificates.ImportAsync(
                        name,
                        await File.ReadAllTextAsync(certFile, ct),
                        await File.ReadAllTextAsync(keyFile, ct),
                        ct))
                    summary.Certificates++;
            } catch (Exception ex) {
                logger.LogWarning(
                    ex, "Skipping the certificate in {Directory}: it could not be imported.", directory);
            }
        }
    }

    /// <summary>
    /// Saves, treating a unique-index collision as success. Two instances can start together on the
    /// upgrade, and both will find the same files; whichever row lands is the same material.
    /// </summary>
    private async Task SaveIgnoringRaceAsync(CancellationToken ct) {
        try {
            await db.SaveChangesAsync(ct);
        } catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) {
            logger.LogInformation("Another instance imported the same legacy state first; keeping theirs.");
            db.ChangeTracker.Clear();
        }
    }

    private sealed class ImportSummary {
        public int SigningKeys { get; set; }
        public int DataProtectionKeys { get; set; }
        public int AcmeAccounts { get; set; }
        public int Certificates { get; set; }
        public bool IsEmpty =>
            SigningKeys == 0 && DataProtectionKeys == 0 && AcmeAccounts == 0 && Certificates == 0;
    }
}
