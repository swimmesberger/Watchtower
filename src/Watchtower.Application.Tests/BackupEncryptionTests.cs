using System.Security.Cryptography;
using System.Text;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Pins the backup encryption to the OpenSSL <c>enc</c> container format (ADR-0016 §4). The whole
/// point of the format choice is that restore works with stock OpenSSL on a machine where Watchtower
/// does not run — so beyond the round-trip, a ciphertext produced by the real <c>openssl</c> CLI is
/// decrypted here. If that vector ever fails, the documented restore command is broken.
/// </summary>
public sealed class BackupEncryptionTests {
    [Fact]
    public void EncryptionRoundTripsThroughTheOwnDecryptor() {
        var plaintext = Encoding.UTF8.GetBytes("some volume tarball bytes, long enough for several AES blocks…");
        var container = new MemoryStream();
        using (var encrypting = BackupEncryption.CreateEncryptingStream(container, "a passphrase"))
            encrypting.Write(plaintext);

        container.Position = 0;
        using var decrypting = BackupEncryption.CreateDecryptingStream(container, "a passphrase");
        var output = new MemoryStream();
        decrypting.CopyTo(output);

        Assert.Equal(plaintext, output.ToArray());
    }

    [Fact]
    public void ACiphertextWrittenByTheOpensslCliDecrypts() {
        // printf 'watchtower interop vector' |
        //   openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 -pass pass:correct-horse -base64
        var container = Convert.FromBase64String(
            "U2FsdGVkX1/Du4ZRYz+PPzWk9nGQ7NQAPYm55PEtxHwVctFqQxpXmvVqUUDuHdV9");

        using var decrypting = BackupEncryption.CreateDecryptingStream(
            new MemoryStream(container), "correct-horse");
        var output = new MemoryStream();
        decrypting.CopyTo(output);

        Assert.Equal("watchtower interop vector", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void EveryEncryptionSaltsFreshly() {
        var one = new MemoryStream();
        var two = new MemoryStream();
        using (var e = BackupEncryption.CreateEncryptingStream(one, "p")) e.Write("same"u8);
        using (var e = BackupEncryption.CreateEncryptingStream(two, "p")) e.Write("same"u8);

        // Same plaintext, same passphrase — different salt, therefore different bytes.
        Assert.NotEqual(one.ToArray(), two.ToArray());
    }

    [Fact]
    public void AWrongPassphraseFailsInsteadOfProducingGarbage() {
        var container = new MemoryStream();
        using (var e = BackupEncryption.CreateEncryptingStream(container, "right"))
            e.Write("payload"u8);

        container.Position = 0;
        using var decrypting = BackupEncryption.CreateDecryptingStream(container, "wrong");
        // CBC+PKCS7: a wrong key surfaces as a padding failure on the final block. (A truly
        // unlucky forged padding is 2^-8 per attempt — irrelevant for a fixed test vector.)
        Assert.Throws<CryptographicException>(() => decrypting.CopyTo(new MemoryStream()));
    }

    [Fact]
    public void AStreamWithoutTheSaltedHeaderIsRejected() {
        var notAContainer = new MemoryStream(Encoding.ASCII.GetBytes("definitely-not-openssl-data"));
        Assert.Throws<InvalidDataException>(
            () => BackupEncryption.CreateDecryptingStream(notAContainer, "p"));
    }
}
