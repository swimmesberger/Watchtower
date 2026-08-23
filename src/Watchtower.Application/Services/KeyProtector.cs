using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Encrypts the private keys the proxy/auth plane keeps in the database at rest — ADR-0024's
/// consequence "keys in the database raise the stakes of a database dump".
/// </summary>
/// <remarks>
/// <para>
/// AES-256-GCM under a key derived with HKDF-SHA256 from <c>Watchtower:Auth:KeyProtectionSecret</c>, one
/// derived key per <em>purpose</em> so a blob cannot be moved between rows that mean different things —
/// a certificate's key pasted into the signing-key row decrypts to nothing rather than to a working key.
/// The stored form is <c>nonce ‖ ciphertext ‖ tag</c>, which needs no framing: both ends are fixed-width.
/// </para>
/// <para>
/// The secret is <b>optional</b>, and that is a deliberate trade rather than an oversight. Requiring it
/// would make the PostgreSQL upgrade two decisions instead of one, and without it the exposure is exactly
/// the one the key files on the data volume already had — anyone who can read the store can read the
/// keys. So an unset secret stores plaintext, marks the row <see cref="None"/>, and says so once at
/// startup. What is <em>not</em> allowed is the silent failure in the other direction: a row written under
/// a secret that has since been lost or changed throws, because quietly regenerating the material behind
/// it would sign every session out and re-order every certificate with nothing in the log to connect the
/// two events.
/// </para>
/// <para>
/// Rotation is by rewrite, not in place: a row still marked <see cref="None"/> after a secret is
/// configured keeps working, and is protected the next time something writes it. Re-encrypting the whole
/// table on the startup that first sees a secret would put a write of every private key onto the path
/// that has to come up before Kestrel serves.
/// </para>
/// </remarks>
public sealed class KeyProtector {
    /// <summary>The marker on a row whose key is stored as-is, because no secret is configured.</summary>
    public const string None = "none";

    /// <summary>The marker on a row encrypted by this class. Versioned so a future scheme can coexist.</summary>
    public const string AesGcmV1 = "aesgcm-v1";

    /// <summary>The default purpose — used when a caller does not separate its own key space.</summary>
    public const string DefaultPurpose = "watchtower";

    /// <summary>
    /// HKDF's salt. A constant rather than a per-row random value on purpose: the salt's job here is
    /// domain separation from any other use of the same secret, not per-message uniqueness (the nonce
    /// does that), and a random salt would have to be stored next to the ciphertext it protects.
    /// </summary>
    private static readonly byte[] Salt = "watchtower/key-protection/v1"u8.ToArray();

    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    private readonly byte[]? _secret;
    private readonly ConcurrentDictionary<string, byte[]> _derived = new(StringComparer.Ordinal);

    public KeyProtector(IOptionsMonitor<WatchtowerOptions> options, ILogger<KeyProtector> logger) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        // Read once. The secret decides how every existing row is readable, so changing it while the
        // process runs would only produce a store half of which it can no longer open; it is an
        // environment decision applied at start, like the connection string.
        var configured = options.CurrentValue.Auth.KeyProtectionSecret;
        if (string.IsNullOrWhiteSpace(configured)) {
            _secret = null;
            // Once, and at Warning: an operator who has decided the database is trusted should not have
            // to read this on every log page, but nobody should be able to say they were not told.
            logger.LogWarning(
                "Private keys (certificates, the ACME account, the identity-assertion signing key) are "
                + "stored unencrypted. Set WATCHTOWER__AUTH__KEYPROTECTIONSECRET to encrypt private keys "
                + "at rest.");
        } else {
            _secret = Encoding.UTF8.GetBytes(configured);
            logger.LogInformation("Private keys in the database are encrypted at rest (AES-256-GCM).");
        }
    }

    /// <summary>Whether a secret is configured, and therefore what a new write is marked with.</summary>
    public bool IsEncrypting => _secret is not null;

    /// <summary>
    /// The <c>Protection</c> value a row written right now carries. Callers store this next to whatever
    /// <see cref="Protect"/> returned, so the reader never has to guess.
    /// </summary>
    public string CurrentProtection => IsEncrypting ? AesGcmV1 : None;

    /// <summary>
    /// Encodes <paramref name="plaintext"/> for storage: encrypted when a secret is configured, verbatim
    /// otherwise. The companion <see cref="CurrentProtection"/> says which.
    /// </summary>
    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose = DefaultPurpose) {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (_secret is null) return plaintext.ToArray();

        var stored = new byte[NonceLength + plaintext.Length + TagLength];
        var nonce = stored.AsSpan(0, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(DeriveKey(purpose), TagLength);
        aes.Encrypt(
            nonce,
            plaintext,
            stored.AsSpan(NonceLength, plaintext.Length),
            stored.AsSpan(NonceLength + plaintext.Length, TagLength));
        return stored;
    }

    /// <summary>Convenience for the PEM callers: protects the UTF-8 bytes of <paramref name="pem"/>.</summary>
    public byte[] ProtectText(string pem, string purpose = DefaultPurpose) {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        var bytes = Encoding.UTF8.GetBytes(pem);
        try {
            return Protect(bytes, purpose);
        } finally {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Recovers what <see cref="Protect"/> stored. <paramref name="protection"/> is the row's own marker,
    /// not this instance's — a <see cref="None"/> row stays readable after a secret is configured.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// The row is encrypted and no secret is configured, the secret does not match, or the ciphertext has
    /// been altered. Never silently empty: material that cannot be read has to stop the thing that needs
    /// it, not be replaced by a fresh key nobody asked for.
    /// </exception>
    public byte[] Unprotect(byte[] stored, string protection, string purpose = DefaultPurpose) {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        switch (protection) {
            case None or "":
                return (byte[])stored.Clone();

            case AesGcmV1 when _secret is null:
                throw new CryptographicException(
                    "This key is encrypted at rest but Watchtower:Auth:KeyProtectionSecret is not set. "
                    + "Restore the secret (WATCHTOWER__AUTH__KEYPROTECTIONSECRET) — without it the stored "
                    + "keys cannot be recovered.");

            case AesGcmV1: {
                if (stored.Length < NonceLength + TagLength)
                    throw new CryptographicException("The stored key is too short to be AES-GCM output.");
                var plaintext = new byte[stored.Length - NonceLength - TagLength];
                try {
                    using var aes = new AesGcm(DeriveKey(purpose), TagLength);
                    aes.Decrypt(
                        stored.AsSpan(0, NonceLength),
                        stored.AsSpan(NonceLength, plaintext.Length),
                        stored.AsSpan(NonceLength + plaintext.Length, TagLength),
                        plaintext);
                } catch (CryptographicException ex) {
                    // GCM's authentication tag does not distinguish "wrong key" from "altered bytes", and
                    // the two have the same remedy, so they get one message that names both.
                    throw new CryptographicException(
                        "A key stored in the database could not be decrypted. Either "
                        + "Watchtower:Auth:KeyProtectionSecret is not the one it was written with, or the "
                        + "row has been altered.", ex);
                }
                return plaintext;
            }

            default:
                throw new CryptographicException(
                    $"Unknown key protection '{protection}'. This database was written by a newer "
                    + "Watchtower.");
        }
    }

    /// <summary>Recovers a PEM that <see cref="ProtectText"/> stored.</summary>
    public string UnprotectText(byte[] stored, string protection, string purpose = DefaultPurpose) {
        var plaintext = Unprotect(stored, protection, purpose);
        try {
            return Encoding.UTF8.GetString(plaintext);
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// The AES key for one purpose. Cached because HKDF is cheap but not free and the handshake path
    /// reaches this once per certificate load.
    /// </summary>
    private byte[] DeriveKey(string purpose) => _derived.GetOrAdd(
        purpose,
        static (key, secret) => HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            secret,
            KeyLength,
            Salt,
            Encoding.UTF8.GetBytes(key)),
        _secret!);
}
