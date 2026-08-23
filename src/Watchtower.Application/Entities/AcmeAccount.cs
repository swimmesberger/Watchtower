namespace Watchtower.Application.Entities;

/// <summary>
/// The ES256 key pair every ACME request is signed with, and the account URL the CA issued for it —
/// one row per ACME directory (ADR-0024 decision 4).
/// </summary>
/// <remarks>
/// One row per directory URL and not one per deployment, because an ACME account exists only at the CA
/// that issued it: pointing Watchtower at a different directory has to produce a fresh key and a fresh
/// registration rather than present one CA's account URL to another.
/// <para>
/// The key is the thing worth not losing. An ACME account is rate-limited per key and accumulates
/// issuance history, so creation is an <c>INSERT … ON CONFLICT DO NOTHING</c> followed by a re-read:
/// two instances starting at once cannot mint two accounts against the same CA.
/// </para>
/// </remarks>
public sealed class AcmeAccount {
    public int Id { get; set; }

    /// <summary>The ACME directory this account belongs to. Unique.</summary>
    public required string DirectoryUrl { get; set; }

    /// <summary>The PKCS#8 account key, protected per <see cref="Protection"/>.</summary>
    public required byte[] PrivateKey { get; set; }

    /// <summary>How <see cref="PrivateKey"/> is encoded — see <c>KeyProtector</c>.</summary>
    public required string Protection { get; set; }

    /// <summary>
    /// The registered account URL (the JWS <c>kid</c>), or null when this key has never been registered
    /// — or when the CA has since forgotten it (<c>accountDoesNotExist</c>).
    /// </summary>
    public string? AccountUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
