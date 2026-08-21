using System.Security.Cryptography;
using System.Text;

namespace Watchtower.Application.Services;

/// <summary>
/// Passphrase encryption for backup archives in the OpenSSL <c>enc</c> container format (ADR-0016 §4):
/// <c>Salted__</c> + 8-byte random salt, then AES-256-CBC with key and IV derived via
/// PBKDF2-HMAC-SHA256 over the passphrase. The point of the format is that restore needs nothing but
/// stock OpenSSL:
/// <code>
/// openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -in backup.tar.gz.enc -out backup.tar.gz
/// </code>
/// </summary>
public static class BackupEncryption {
    /// <summary>PBKDF2 iteration count — mirrored in the documented openssl restore command.</summary>
    public const int Pbkdf2Iterations = 600_000;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("Salted__");

    /// <summary>
    /// Wraps <paramref name="destination"/> in an encrypting stream: writes the <c>Salted__</c> header,
    /// then returns a <see cref="CryptoStream"/> the plaintext is written into. Dispose the returned
    /// stream to flush the final cipher block; <paramref name="destination"/> is left open.
    /// </summary>
    public static Stream CreateEncryptingStream(Stream destination, string passphrase) {
        var salt = RandomNumberGenerator.GetBytes(8);
        destination.Write(Magic);
        destination.Write(salt);
        using var aes = CreateAes(passphrase, salt);
        return new CryptoStream(destination, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
    }

    /// <summary>
    /// Wraps <paramref name="source"/> in a decrypting stream after validating the <c>Salted__</c>
    /// header. Counterpart of <see cref="CreateEncryptingStream"/>; used by the tests to prove the
    /// format round-trips, and available to a future restore surface.
    /// </summary>
    public static Stream CreateDecryptingStream(Stream source, string passphrase) {
        Span<byte> header = stackalloc byte[16];
        source.ReadExactly(header);
        if (!header[..8].SequenceEqual(Magic))
            throw new InvalidDataException("Not an OpenSSL enc container: missing Salted__ header.");
        using var aes = CreateAes(passphrase, header[8..].ToArray());
        return new CryptoStream(source, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
    }

    /// <summary>
    /// Key + IV exactly as <c>openssl enc -pbkdf2 -md sha256</c> derives them: one 48-byte PBKDF2
    /// output split into the 32-byte key and the 16-byte IV.
    /// </summary>
    private static Aes CreateAes(string passphrase, byte[] salt) {
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 48);
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = derived[..32];
        aes.IV = derived[32..];
        return aes;
    }
}
