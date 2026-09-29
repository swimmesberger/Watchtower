using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Notifications.WebPush;

/// <summary>Resolves the VAPID identity every push message is signed with.</summary>
/// <remarks>
/// TODO(elarion#162): generic key management an Elarion Web Push package is expected to own
/// (configuration first, then a persisted pair generated on first use).
/// </remarks>
public interface IVapidKeyProvider {
    /// <summary>The key pair and subject to sign with; generates and persists a pair on first use.</summary>
    ValueTask<VapidCredentials> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Configuration → database → generate. A pair pinned in <c>Watchtower:WebPush</c> wins; otherwise the
/// persisted <see cref="VapidKeyPair"/> row; otherwise a fresh P-256 pair is generated and inserted, so a
/// new installation needs no setup before its first notification.
/// </summary>
/// <remarks>
/// <para>
/// First use can race — two instances, or two requests on one — and must not end with two pairs: a
/// browser subscribed against the loser's public key could never be delivered to. The row's fixed id is
/// the guard: both insert id 1, the primary key refuses the second, and the loser re-reads the winner's
/// pair (the same insert-then-re-read shape as the identity-assertion signing key).
/// </para>
/// <para>
/// The private half is stored through <see cref="KeyProtector"/> under its own purpose, like every other
/// private key in the database (ADR-0024). Scoped: it reads through the scoped context and is cheap enough
/// to resolve per send that caching it would only add an invalidation problem.
/// </para>
/// </remarks>
internal sealed class VapidKeyProvider(
    WatchtowerDbContext db,
    KeyProtector protector,
    IOptionsMonitor<WatchtowerOptions> options,
    TimeProvider time,
    ILogger<VapidKeyProvider> logger) : IVapidKeyProvider {
    /// <summary>The <see cref="KeyProtector"/> purpose — a blob from another key row decrypts to nothing here.</summary>
    internal const string KeyPurpose = "vapid";

    public async ValueTask<VapidCredentials> GetAsync(CancellationToken ct = default) {
        var configured = options.CurrentValue.WebPush;
        var subject = configured.ResolveSubject();
        if (configured.HasConfiguredKeys)
            return new VapidCredentials(configured.PublicKey!.Trim(), configured.PrivateKey!.Trim(), subject);

        var stored = await ReadAsync(ct);
        if (stored is null) {
            await TryCreateAsync(ct);
            stored = await ReadAsync(ct)
                     ?? throw new InvalidOperationException("The VAPID key pair was created but could not be read back.");
        }

        var privateKey = Encoding.UTF8.GetString(protector.Unprotect(stored.PrivateKey, stored.Protection, KeyPurpose));
        return new VapidCredentials(stored.PublicKey, privateKey, subject);
    }

    private Task<VapidKeyPair?> ReadAsync(CancellationToken ct) =>
        db.VapidKeyPairs.AsNoTracking().FirstOrDefaultAsync(k => k.Id == VapidKeyPair.SingletonId, ct);

    private async Task TryCreateAsync(CancellationToken ct) {
        var (publicKey, privateKey) = Generate();
        var row = new VapidKeyPair {
            PublicKey = publicKey,
            PrivateKey = protector.Protect(Encoding.UTF8.GetBytes(privateKey), KeyPurpose),
            Protection = protector.CurrentProtection,
            CreatedAt = time.GetUtcNow(),
        };
        db.VapidKeyPairs.Add(row);
        try {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Generated the VAPID key pair for Web Push notifications.");
        } catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) {
            // Lost the first-use race; the caller re-reads the pair that won.
            logger.LogInformation("Another request created the VAPID key pair first; using theirs.");
        } finally {
            // Never leave the row tracked: on the losing path it would be re-inserted by the next save
            // anything else in this scope makes.
            db.Entry(row).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// A fresh P-256 pair in the encoding browsers (<c>applicationServerKey</c>) and push services expect:
    /// the public key as the 65-byte uncompressed point, the private key as the 32-byte scalar, both
    /// base64url without padding.
    /// </summary>
    internal static (string PublicKey, string PrivateKey) Generate() {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        try {
            Span<byte> publicKey = stackalloc byte[65];
            publicKey[0] = 0x04;
            CopyRightAligned(p.Q.X!, publicKey.Slice(1, 32));
            CopyRightAligned(p.Q.Y!, publicKey.Slice(33, 32));
            Span<byte> privateKey = stackalloc byte[32];
            CopyRightAligned(p.D!, privateKey);
            var result = (Base64Url.EncodeToString(publicKey), Base64Url.EncodeToString(privateKey));
            CryptographicOperations.ZeroMemory(privateKey);
            return result;
        } finally {
            CryptographicOperations.ZeroMemory(p.D);
        }
    }

    // Field elements are fixed-width big-endian; pad on the left should an export come back shorter.
    private static void CopyRightAligned(byte[] source, Span<byte> target) {
        target.Clear();
        source.AsSpan().CopyTo(target[(target.Length - source.Length)..]);
    }
}
