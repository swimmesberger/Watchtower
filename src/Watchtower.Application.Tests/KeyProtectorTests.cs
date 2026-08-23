using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Encryption at rest for the private keys that moved into the database (ADR-0024). The properties are
/// the ones a key store has to have and a cache does not: what comes back is exactly what went in, an
/// altered ciphertext is refused rather than decoded, and material written under a secret that is now
/// gone produces an error naming the secret instead of a fresh key nobody asked for.
/// </summary>
public sealed class KeyProtectorTests {
    private const string Secret = "a-long-enough-passphrase-for-a-test";
    private const string Purpose = "proxy-certificate";

    [Fact]
    public void ProtectAndUnprotect_RoundTrip() {
        var protector = Protector(Secret);

        var stored = protector.ProtectText("-----BEGIN PRIVATE KEY-----\nabc\n", Purpose);

        Assert.Equal(KeyProtector.AesGcmV1, protector.CurrentProtection);
        // The plaintext must not be recoverable by reading the column.
        Assert.DoesNotContain("BEGIN PRIVATE KEY", Encoding.UTF8.GetString(stored));
        Assert.Equal(
            "-----BEGIN PRIVATE KEY-----\nabc\n",
            protector.UnprotectText(stored, KeyProtector.AesGcmV1, Purpose));
    }

    /// <summary>
    /// A fresh nonce per write, so two rows holding the same key are not visibly the same key — and so
    /// AES-GCM is used the one way it is safe to use.
    /// </summary>
    [Fact]
    public void EachProtection_UsesItsOwnNonce() {
        var protector = Protector(Secret);

        var first = protector.ProtectText("same key", Purpose);
        var second = protector.ProtectText("same key", Purpose);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Domain separation: a certificate's key blob dropped into the signing-key row must not decrypt.
    /// Otherwise the encryption would protect the database file and nothing about the rows in it.
    /// </summary>
    [Fact]
    public void AnotherPurpose_CannotReadIt() {
        var protector = Protector(Secret);
        var stored = protector.ProtectText("the certificate key", Purpose);

        Assert.Throws<CryptographicException>(
            () => protector.UnprotectText(stored, KeyProtector.AesGcmV1, "identity-assertion"));
    }

    /// <summary>
    /// The authentication tag is the whole reason for GCM here: a row an attacker with write access has
    /// edited must fail loudly rather than yield a key of their choosing.
    /// </summary>
    [Fact]
    public void ATamperedCiphertext_IsRefused() {
        var protector = Protector(Secret);
        var stored = protector.ProtectText("the key", Purpose);

        stored[^1] ^= 0xFF;

        var error = Assert.Throws<CryptographicException>(
            () => protector.UnprotectText(stored, KeyProtector.AesGcmV1, Purpose));
        Assert.Contains("could not be decrypted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortCiphertext_IsRefused() {
        var protector = Protector(Secret);

        Assert.Throws<CryptographicException>(
            () => protector.Unprotect([1, 2, 3], KeyProtector.AesGcmV1, Purpose));
    }

    /// <summary>
    /// The default posture: no secret, keys stored exactly as the files on the data volume were, and one
    /// warning at startup rather than a refusal to run. Optional is the deliberate choice — see the class
    /// remarks — so it needs a test that says so.
    /// </summary>
    [Fact]
    public void WithNoSecret_KeysAreStoredAsIs_AndSaidSoAbout() {
        var log = new CollectingLogger();
        var protector = Protector(secret: null, log);

        var stored = protector.ProtectText("plain key", Purpose);

        Assert.False(protector.IsEncrypting);
        Assert.Equal(KeyProtector.None, protector.CurrentProtection);
        Assert.Equal("plain key", Encoding.UTF8.GetString(stored));
        Assert.Equal("plain key", protector.UnprotectText(stored, KeyProtector.None, Purpose));
        Assert.Contains(
            log.Warnings, w => w.Contains("WATCHTOWER__AUTH__KEYPROTECTIONSECRET", StringComparison.Ordinal));
    }

    /// <summary>
    /// Rotation is by rewrite: a row written before the secret was configured keeps working afterwards,
    /// and is protected the next time something writes it. Re-encrypting the whole table on the startup
    /// that first sees a secret would put a write of every private key on the path that has to come up
    /// before Kestrel serves.
    /// </summary>
    [Fact]
    public void ARowWrittenWithoutASecret_StaysReadableAfterOneIsConfigured() {
        var stored = Protector(secret: null).ProtectText("plain key", Purpose);
        var configured = Protector(Secret);

        Assert.Equal("plain key", configured.UnprotectText(stored, KeyProtector.None, Purpose));
        Assert.Equal(KeyProtector.AesGcmV1, configured.CurrentProtection);
    }

    /// <summary>
    /// The failure that must never be silent. Regenerating the material behind an unreadable row would
    /// sign every session out and re-order every certificate, with nothing in the log connecting the two.
    /// </summary>
    [Fact]
    public void WithoutTheSecretItWasWrittenWith_ItFailsLoudly() {
        var stored = Protector(Secret).ProtectText("the key", Purpose);

        var missing = Assert.Throws<CryptographicException>(
            () => Protector(secret: null).UnprotectText(stored, KeyProtector.AesGcmV1, Purpose));
        Assert.Contains("KeyProtectionSecret", missing.Message, StringComparison.Ordinal);

        var wrong = Assert.Throws<CryptographicException>(
            () => Protector("a-different-passphrase").UnprotectText(stored, KeyProtector.AesGcmV1, Purpose));
        Assert.Contains("could not be decrypted", wrong.Message, StringComparison.Ordinal);
    }

    /// <summary>A database written by a newer Watchtower is a diagnosis, not a corrupt key.</summary>
    [Fact]
    public void AnUnknownProtection_SaysSo() {
        var error = Assert.Throws<CryptographicException>(
            () => Protector(Secret).Unprotect([1, 2, 3], "chacha-v9", Purpose));

        Assert.Contains("newer Watchtower", error.Message, StringComparison.Ordinal);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private static KeyProtector Protector(string? secret, ILogger<KeyProtector>? logger = null) =>
        new(
            new StaticOptionsMonitor(new WatchtowerOptions {
                Auth = new AuthOptions { KeyProtectionSecret = secret },
            }),
            logger ?? NullLogger<KeyProtector>.Instance);

    private sealed class StaticOptionsMonitor(WatchtowerOptions value) : IOptionsMonitor<WatchtowerOptions> {
        public WatchtowerOptions CurrentValue { get; } = value;
        public WatchtowerOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<WatchtowerOptions, string?> listener) => null;
    }

    private sealed class CollectingLogger : ILogger<KeyProtector> {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
