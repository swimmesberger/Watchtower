namespace Watchtower.Application.Entities;

/// <summary>
/// A long-lived private key Watchtower signs with, keyed by what it is for — ADR-0024 decision 4. One
/// purpose exists today (<see cref="IdentityAssertionPurpose"/>); the table is keyed by purpose rather
/// than holding a single row so a second signing key does not need a second table.
/// </summary>
/// <remarks>
/// The row is what makes an assertion minted on one instance verifiable on every other: every instance
/// reads the same key, publishes the same JWKS and stamps the same <c>kid</c>. Created with
/// <c>INSERT … ON CONFLICT DO NOTHING</c> and re-read, so two instances starting together agree on one
/// key rather than each generating its own.
/// </remarks>
public sealed class SigningKey {
    /// <summary>The ES256 key behind <c>X-Watchtower-Jwt</c> and the public JWKS.</summary>
    public const string IdentityAssertionPurpose = "identity-assertion";

    /// <summary>What the key signs. The primary key.</summary>
    public required string Purpose { get; set; }

    /// <summary>The PKCS#8 private key, protected per <see cref="Protection"/>.</summary>
    public required byte[] PrivateKey { get; set; }

    /// <summary>How <see cref="PrivateKey"/> is encoded — see <c>KeyProtector</c>.</summary>
    public required string Protection { get; set; }

    /// <summary>
    /// The RFC 7638 thumbprint of the public half — the <c>kid</c> in both the JWT header and the JWKS.
    /// Derived from the key and stored so an operator can tell two rows apart without decrypting either.
    /// </summary>
    public required string KeyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
