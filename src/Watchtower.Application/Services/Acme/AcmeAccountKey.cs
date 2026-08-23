using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Watchtower.Application.Persistence;
// The entity, aliased: `AcmeAccount` in this namespace is already the CA's account *resource* (the JSON
// object RFC 8555 returns), and the row is a different thing that happens to want the same word.
using AcmeAccountRow = Watchtower.Application.Entities.AcmeAccount;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// The ES256 key pair every ACME request is signed with, and the account URL the CA issued for it —
/// ADR-0022, moved into the database by ADR-0024. Held in memory for the life of one
/// <see cref="AcmeSession"/>; the row behind it is <see cref="AcmeAccountStore"/>'s.
/// </summary>
/// <remarks>
/// An ACME account is rate-limited per key and accumulates issuance history, so the one property worth
/// paying for is <em>never minting a second one</em>. A row per directory URL and an
/// <c>INSERT … ON CONFLICT DO NOTHING</c> give that across instances, where the old
/// <c>FileMode.CreateNew</c> only gave it across processes sharing one volume.
/// <para>
/// The account URL is written through to the row as soon as the CA issues it, so the next start — on any
/// instance — signs with <c>kid</c> straight away instead of re-registering.
/// </para>
/// </remarks>
public sealed class AcmeAccountKey : IDisposable {
    private readonly AcmeAccountStore? _store;
    private readonly Lock _gate = new();
    private string? _accountUrl;
    private bool _disposed;

    private AcmeAccountKey(AcmeAccountStore? store, ECDsa key, string directoryUrl, string? accountUrl) {
        _store = store;
        Key = key;
        DirectoryUrl = directoryUrl;
        _accountUrl = accountUrl;
    }

    internal static AcmeAccountKey Backed(
        AcmeAccountStore store, ECDsa key, string directoryUrl, string? accountUrl) =>
        new(store, key, directoryUrl, accountUrl);

    /// <summary>
    /// An account key with no row behind it: <see cref="SetAccountUrl"/> and
    /// <see cref="ClearAccountUrl"/> change only this instance. For exercising the ACME wire protocol,
    /// which is about what a CA is sent and has nothing to say about where the key was stored.
    /// </summary>
    public static AcmeAccountKey Detached(ECDsa key, string directoryUrl, string? accountUrl = null) {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryUrl);
        return new AcmeAccountKey(store: null, key, directoryUrl, accountUrl);
    }

    /// <summary>The signing key. Owned by this instance and disposed with it.</summary>
    public ECDsa Key { get; }

    /// <summary>The ACME directory this account belongs to.</summary>
    public string DirectoryUrl { get; }

    /// <summary>
    /// The registered account URL (the JWS <c>kid</c>), or null when this key has never been registered
    /// — or when the CA has since forgotten it.
    /// </summary>
    public string? AccountUrl {
        get { lock (_gate) return _accountUrl; }
    }

    /// <summary>Records the account URL the CA issued, so the next start signs with <c>kid</c> straight away.</summary>
    public Task SetAccountUrlAsync(string url, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        lock (_gate) {
            if (string.Equals(_accountUrl, url, StringComparison.Ordinal)) return Task.CompletedTask;
            _accountUrl = url;
        }
        return _store?.WriteAccountUrlAsync(DirectoryUrl, url, ct) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Forgets the account URL — the <c>accountDoesNotExist</c> path, where the CA no longer knows the
    /// account this key was registered as and the only way forward is to register it again.
    /// </summary>
    public Task ClearAccountUrlAsync(CancellationToken ct = default) {
        lock (_gate) {
            if (_accountUrl is null) return Task.CompletedTask;
            _accountUrl = null;
        }
        return _store?.WriteAccountUrlAsync(DirectoryUrl, null, ct) ?? Task.CompletedTask;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        Key.Dispose();
    }
}

/// <summary>
/// The <c>acme_accounts</c> table: one key pair and registration per ACME directory URL — ADR-0024
/// decision 4.
/// </summary>
/// <remarks>
/// The directory URL is the key and not an incidental column. An ACME account exists only at the CA
/// that issued it, so switching from Let's Encrypt to an internal CA has to produce a fresh key and a
/// fresh registration rather than present one CA's account URL to another; keying the row that way makes
/// that a lookup rather than a rule somebody has to remember.
/// <para>
/// Creation is deliberately not read-then-write. Two instances starting together would both find nothing
/// and both generate a P-256 pair, and the CA would end up with two accounts for one deployment — each
/// with its own rate-limit budget and its own half of the issuance history. The insert is therefore
/// unconditional and lets the unique index decide, after which both instances re-read and use the same
/// key.
/// </para>
/// </remarks>
public sealed class AcmeAccountStore(
    IServiceScopeFactory scopeFactory,
    KeyProtector protector,
    TimeProvider time,
    ILogger<AcmeAccountStore> logger) {
    /// <summary>The <see cref="KeyProtector"/> purpose the account keys are encrypted under.</summary>
    internal const string KeyPurpose = "acme-account";

    /// <summary>
    /// Loads the account for one ACME directory, creating one on first use.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The stored key could not be read — a wrong or missing key-protection secret, or an altered row.
    /// Fatal rather than replaced: quietly minting a new key abandons the account the CA associates with
    /// this deployment, including whatever rate-limit allowance it has earned.
    /// </exception>
    public async Task<AcmeAccountKey> LoadOrCreateAsync(string directoryUrl, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryUrl);

        var existing = await ReadAsync(directoryUrl, ct);
        if (existing is null) {
            await TryCreateAsync(directoryUrl, ct);
            // Re-read unconditionally: this instance may have lost the insert race, and the winner's key
            // is the one the CA will know the account by.
            existing = await ReadAsync(directoryUrl, ct)
                       ?? throw new InvalidOperationException(
                           $"The ACME account for {directoryUrl} was created but could not be read back.");
        }

        var key = ECDsa.Create();
        try {
            var pem = protector.UnprotectText(existing.PrivateKey, existing.Protection, KeyPurpose);
            try {
                key.ImportFromPem(pem);
            } catch (Exception ex) when (ex is ArgumentException or CryptographicException) {
                // Re-thrown as a CryptographicException because ImportFromPem reports a malformed PEM as
                // an ArgumentException about its parameter, which describes the call rather than the
                // problem.
                throw new CryptographicException(
                    $"The stored ACME account key for {directoryUrl} could not be read. Delete the row to "
                    + "register a new account, or restore the key-protection secret it was written with.",
                    ex);
            }
            await ReprotectAsync(existing, pem, ct);
            return AcmeAccountKey.Backed(this, key, directoryUrl, existing.AccountUrl);
        } catch {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encrypts a row that was written before a key-protection secret existed. On load rather than by a
    /// migration pass, because this is the only moment the plaintext is in hand anyway — and because an
    /// operator who adopts the secret expects the keys to become encrypted without a separate step.
    /// </summary>
    private async Task ReprotectAsync(AcmeAccountRow row, string pem, CancellationToken ct) {
        if (!protector.IsEncrypting || row.Protection != KeyProtector.None) return;
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var updated = await db.AcmeAccounts
                // Guarded on the row still being unprotected, so two instances starting together do not
                // both rewrite it — and, more to the point, so this cannot overwrite a row somebody
                // re-encrypted under a different secret in between.
                .Where(a => a.DirectoryUrl == row.DirectoryUrl && a.Protection == KeyProtector.None)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(a => a.PrivateKey, protector.ProtectText(pem, KeyPurpose))
                        .SetProperty(a => a.Protection, protector.CurrentProtection),
                    ct);
            if (updated > 0)
                logger.LogInformation(
                    "Encrypted the stored ACME account key for {DirectoryUrl} at rest.", row.DirectoryUrl);
        } catch (Exception ex) {
            // Not fatal: the key was read fine and issuance works. The next start tries again.
            logger.LogWarning(
                ex, "Could not encrypt the stored ACME account key for {DirectoryUrl} at rest.",
                row.DirectoryUrl);
        }
    }

    /// <summary>
    /// Records (or clears) the account URL for a directory. Fire-and-forget from the caller's point of
    /// view: the account exists at the CA either way, and the cost of losing this write is one extra
    /// registration on the next start, which the CA answers with the same account URL for the same key
    /// (RFC 8555 §7.3).
    /// </summary>
    internal async Task WriteAccountUrlAsync(
        string directoryUrl, string? accountUrl, CancellationToken ct = default) {
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.AcmeAccounts
                .Where(a => a.DirectoryUrl == directoryUrl)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.AccountUrl, accountUrl), ct);
        } catch (Exception ex) {
            logger.LogWarning(
                ex, "Could not persist the ACME account registration for {DirectoryUrl}.", directoryUrl);
        }
    }

    private async Task<AcmeAccountRow?> ReadAsync(string directoryUrl, CancellationToken ct) {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AcmeAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.DirectoryUrl == directoryUrl, ct);
    }

    /// <summary>
    /// Generates and stores a key pair, unless another instance got there first. The unique index on
    /// <c>directory_url</c> is the race guard — an existence check would not be one.
    /// </summary>
    private async Task TryCreateAsync(string directoryUrl, CancellationToken ct) {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.AcmeAccounts.Add(new AcmeAccountRow {
            DirectoryUrl = directoryUrl,
            PrivateKey = protector.ProtectText(generated.ExportPkcs8PrivateKeyPem(), KeyPurpose),
            Protection = protector.CurrentProtection,
            AccountUrl = null,
            CreatedAt = time.GetUtcNow(),
        });

        try {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Generated a new ES256 ACME account key for {DirectoryUrl}.", directoryUrl);
        } catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) {
            logger.LogInformation(
                "Another instance created the ACME account for {DirectoryUrl} first; using theirs.",
                directoryUrl);
        }
    }
}
