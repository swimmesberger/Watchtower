using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The ASP.NET data-protection key ring in the database (ADR-0024): shared across instances, and
/// encrypted at rest under the same secret as every other private key Watchtower stores.
/// </summary>
/// <remarks>
/// Driven through the real <see cref="IKeyManager"/> and <see cref="IDataProtector"/> rather than
/// against the encryptor in isolation. The thing worth knowing is not that a class round-trips an
/// <c>XElement</c> — it is that the key manager writes what our encryptor produced, resolves our
/// decryptor by the type name it recorded, and hands back a protector whose payloads another instance
/// can read.
/// </remarks>
public sealed class DataProtectionRingTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Secret = "a-long-enough-passphrase-for-a-test";
    private const string Purpose = "watchtower-ring-test";

    /// <summary>
    /// The cross-instance property the ring moved into the database for: a payload protected on one
    /// node is readable on another, with the encryption at rest in the way.
    /// </summary>
    [Fact]
    public async Task WithASecret_APayloadProtectedOnOneInstance_IsReadableOnAnother() {
        using var first = StartEncrypting();
        using var second = first.Restart(("Watchtower:Auth:KeyProtectionSecret", Secret));

        var payload = Protector(first).Protect("a session's protected payload");

        Assert.Equal("a session's protected payload", Protector(second).Unprotect(payload));
    }

    /// <summary>
    /// The point of encrypting it: a dump of <c>data_protection_keys</c> must not contain the key
    /// material. Asserted on the raw column text, because that is exactly what a dump contains.
    /// </summary>
    [Fact]
    public async Task WithASecret_TheStoredRingHoldsNoReadableKeyMaterial() {
        using var host = StartEncrypting();
        // Forces the key manager to generate and persist a key rather than asserting on an empty ring.
        Protector(host).Protect("anything");

        var xml = await RingXmlAsync(host);

        var element = Assert.Single(xml);
        Assert.Contains("encryptedKey", element, StringComparison.Ordinal);
        Assert.Contains(nameof(KeyProtectorXmlDecryptor), element, StringComparison.Ordinal);
        // `masterKey` is the element holding the actual secret material in an unencrypted ring.
        Assert.DoesNotContain("masterKey", element, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Without a secret the ring is ASP.NET's default plaintext — the same exposure the key directory
    /// on the data volume had, and the reason the host warns about it once at startup.
    /// </summary>
    [Fact]
    public async Task WithoutASecret_TheRingIsStoredAsAspNetWritesIt() {
        using var host = AuthTestHost.Start();
        Protector(host).Protect("anything");

        var element = Assert.Single(await RingXmlAsync(host));

        Assert.DoesNotContain("encryptedKey", element, StringComparison.Ordinal);
        Assert.Contains("masterKey", element, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A ring written before the secret was configured keeps loading afterwards — its elements carry no
    /// wrapper for a decryptor to be looked up by. Only newly generated keys are encrypted; the ring is
    /// append-only, and rewriting it is how a ring loses keys.
    /// </summary>
    [Fact]
    public async Task ARingWrittenWithoutASecret_StillLoadsAfterOneIsConfigured() {
        using var before = AuthTestHost.Start();
        var payload = Protector(before).Protect("protected before the secret existed");

        using var after = before.Restart(("Watchtower:Auth:KeyProtectionSecret", Secret));

        Assert.Equal("protected before the secret existed", Protector(after).Unprotect(payload));
    }

    /// <summary>
    /// The failure that must never be silent. Material encrypted under a secret that has since been
    /// removed has to stop the process, not quietly be replaced — an operator would otherwise see every
    /// session ending at once and every certificate reordered, with nothing saying why.
    /// </summary>
    /// <remarks>
    /// The host does not even get as far as the ring: the signing key is read first and refuses on the
    /// same grounds, which is the right shape — one missing secret is one startup failure naming it,
    /// not a series of partial degradations. The ring's own half is asserted below, on the decryptor
    /// the key manager would have invoked.
    /// </remarks>
    [Fact]
    public void WithoutTheSecretItWasWrittenWith_TheHostRefusesToStart() {
        using var encrypting = StartEncrypting();
        Protector(encrypting).Protect("protected under the secret");

        var error = Assert.ThrowsAny<Exception>(() => encrypting.Restart().Dispose());

        Assert.Contains("KeyProtectionSecret", Flatten(error), StringComparison.Ordinal);
    }

    /// <summary>
    /// The ring half of the same guarantee, at the seam the key manager uses: an encrypted element and
    /// no secret is an exception naming the secret, never an element quietly treated as empty.
    /// </summary>
    [Fact]
    public async Task AnEncryptedRingElement_WithoutTheSecret_FailsLoudly() {
        using var encrypting = StartEncrypting();
        Protector(encrypting).Protect("anything");
        var element = System.Xml.Linq.XElement.Parse(Assert.Single(await RingXmlAsync(encrypting)));
        var encrypted = element.Descendants()
            .First(e => e.Name.LocalName == KeyProtectorXmlEncryptor.ElementName);

        using var withoutSecret = AuthTestHost.Start();
        var decryptor = new KeyProtectorXmlDecryptor(withoutSecret.Services);

        var error = Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => decryptor.Decrypt(encrypted));
        Assert.Contains("KeyProtectionSecret", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Registered on the secret, not unconditionally: with none configured the key manager keeps
    /// ASP.NET's default, which is what leaves an existing plaintext ring readable.
    /// </summary>
    [Fact]
    public void TheEncryptor_IsRegisteredOnlyWhenASecretIsConfigured() {
        using var encrypting = StartEncrypting();
        using var plain = AuthTestHost.Start();

        Assert.IsType<KeyProtectorXmlEncryptor>(KeyManagement(encrypting).XmlEncryptor);
        Assert.Null(KeyManagement(plain).XmlEncryptor);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private static AuthTestHost StartEncrypting() =>
        AuthTestHost.Start(("Watchtower:Auth:KeyProtectionSecret", Secret));

    private static IDataProtector Protector(AuthTestHost host) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

    private static KeyManagementOptions KeyManagement(AuthTestHost host) =>
        host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<KeyManagementOptions>>().Value;

    private static async Task<List<string>> RingXmlAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.DataProtectionKeys.AsNoTracking()
            .Select(k => k.Xml!)
            .ToListAsync(Ct);
    }

    /// <summary>
    /// The whole exception chain as text. Data protection wraps a failing decryptor several layers deep,
    /// and what is being asserted is that the operator-facing cause survives to the top.
    /// </summary>
    private static string Flatten(Exception error) {
        var text = new System.Text.StringBuilder();
        for (var current = error; current is not null; current = current.InnerException)
            text.Append(current.Message).Append(' ');
        return text.ToString();
    }
}
