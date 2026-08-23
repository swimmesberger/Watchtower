using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Where the identity-assertion signing key lives since ADR-0024, and the property that move exists for:
/// every instance mints under the same <c>kid</c> and publishes the same JWKS, so an assertion minted on
/// one node verifies against the document another node served.
/// </summary>
/// <remarks>
/// The claims, the algorithm pinning and the validation rules are <see cref="AuthTokenSignerTests"/>'s;
/// these are only about the key's storage and identity.
/// </remarks>
public sealed class SigningKeyStorageTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheKey_IsARow_CreatedOnFirstStart() {
        using var host = AuthTestHost.Start();

        var row = await RowAsync(host);
        Assert.NotNull(row);
        Assert.Equal(SigningKey.IdentityAssertionPurpose, row.Purpose);
        Assert.NotEmpty(row.PrivateKey);
        // Stored on the row as well as derived, so an operator can tell two keys apart without
        // decrypting either.
        Assert.Equal(host.Services.GetRequiredService<AuthTokenSigner>().KeyId, row.KeyId);
    }

    /// <summary>
    /// Two instances, one key. A second key would mint assertions carrying a <c>kid</c> that is not in
    /// the JWKS the first instance serves — which an app sees as a token it cannot verify, intermittently,
    /// depending on which node answered.
    /// </summary>
    [Fact]
    public async Task TwoInstances_AgreeOnTheKeyAndTheJwks() {
        using var first = AuthTestHost.Start();
        using var second = first.Restart();

        var one = first.Services.GetRequiredService<AuthTokenSigner>();
        var other = second.Services.GetRequiredService<AuthTokenSigner>();

        Assert.Equal(one.KeyId, other.KeyId);
        Assert.Equal(
            Canonical(one.JwksDocument), Canonical(other.JwksDocument));
        Assert.Equal(1, await CountAsync(first));
    }

    /// <summary>
    /// The restart property, stated against the row rather than a file: the same <c>kid</c> comes back,
    /// so an app that pinned the JWKS does not have to refetch after every deployment.
    /// </summary>
    [Fact]
    public async Task ARestart_KeepsTheSameKid() {
        using var host = AuthTestHost.Start();
        var keyId = host.Services.GetRequiredService<AuthTokenSigner>().KeyId;

        using var restarted = host.Restart();

        Assert.Equal(keyId, restarted.Services.GetRequiredService<AuthTokenSigner>().KeyId);
        Assert.Equal(1, await CountAsync(host));
    }

    /// <summary>
    /// With a key-protection secret configured, the column must not be a readable PEM — that is the whole
    /// difference between a database dump and a key compromise.
    /// </summary>
    [Fact]
    public async Task WithAKeyProtectionSecret_TheStoredKeyIsEncrypted() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Auth:KeyProtectionSecret", "a-long-enough-passphrase-for-a-test"));

        var row = await RowAsync(host);
        Assert.Equal(KeyProtector.AesGcmV1, row!.Protection);
        Assert.DoesNotContain(
            "PRIVATE KEY", System.Text.Encoding.UTF8.GetString(row.PrivateKey), StringComparison.Ordinal);
        // And it is still the key the signer is using.
        Assert.Equal(host.Services.GetRequiredService<AuthTokenSigner>().KeyId, row.KeyId);
    }

    /// <summary>
    /// Adopting the secret on a running installation must not need a migration step. The key is already
    /// in hand at the moment it is loaded, so the row is encrypted there — the alternative is a
    /// deployment that sets the secret, sees no warning, and still has a plaintext key in the database.
    /// </summary>
    [Fact]
    public async Task AdoptingTheSecretLater_EncryptsTheExistingKey_OnTheNextStart() {
        using var plain = AuthTestHost.Start();
        var keyId = plain.Services.GetRequiredService<AuthTokenSigner>().KeyId;
        Assert.Equal(KeyProtector.None, (await RowAsync(plain))!.Protection);

        using var encrypting = plain.Restart(
            ("Watchtower:Auth:KeyProtectionSecret", "a-long-enough-passphrase-for-a-test"));

        var row = await RowAsync(encrypting);
        Assert.Equal(KeyProtector.AesGcmV1, row!.Protection);
        // The same key, encrypted — not a new one. A new one would invalidate every assertion an
        // operator's apps have cached a kid for.
        Assert.Equal(keyId, row.KeyId);
        Assert.Equal(keyId, encrypting.Services.GetRequiredService<AuthTokenSigner>().KeyId);
    }

    [Fact]
    public async Task WithoutASecret_TheKeyIsStoredAsThePemFileWas() {
        using var host = AuthTestHost.Start();

        var row = await RowAsync(host);
        Assert.Equal(KeyProtector.None, row!.Protection);
        Assert.Contains(
            "PRIVATE KEY", System.Text.Encoding.UTF8.GetString(row.PrivateKey), StringComparison.Ordinal);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private static async Task<SigningKey?> RowAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.SigningKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Purpose == SigningKey.IdentityAssertionPurpose, Ct);
    }

    private static async Task<int> CountAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>().SigningKeys.CountAsync(Ct);
    }

    /// <summary>Compared as parsed JSON, so key order in the rendered document is not the assertion.</summary>
    private static string Canonical(string jwks) =>
        JsonSerializer.Serialize(JsonDocument.Parse(jwks).RootElement);
}
