using System.Security.Cryptography;
using System.Text;

namespace Watchtower.Application.Services;

/// <summary>
/// The shape every Watchtower-issued bearer token shares: a recognizable prefix, 256 bits of
/// cryptographic randomness in unpadded base64url, and a constant-time comparison for verifying one.
/// </summary>
/// <remarks>
/// Extracted from <see cref="AppApiTokens"/> when the release webhook needed a second token
/// (<c>wtrel_</c>) with identical properties. One generator rather than two: the entropy, the encoding
/// and the "safe in a header, a URL and a <c>.env</c> file" property are the same requirement in both
/// places, and a second copy is how one of them quietly ends up with fewer bits.
/// </remarks>
public static class BearerTokens {
    /// <summary>Number of random bytes behind each token (256 bits).</summary>
    private const int EntropyBytes = 32;

    /// <summary>The <c>Authorization</c> scheme every one of these tokens is presented under.</summary>
    private const string BearerScheme = "Bearer ";

    /// <summary>
    /// Creates a new token: <paramref name="prefix"/> followed by <see cref="EntropyBytes"/>
    /// cryptographically random bytes encoded as unpadded base64url.
    /// </summary>
    public static string Generate(string prefix) {
        var bytes = RandomNumberGenerator.GetBytes(EntropyBytes);
        var encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return prefix + encoded;
    }

    /// <summary>
    /// Extracts a token of the given shape from an <c>Authorization</c> header value. Returns null when
    /// the header is absent, uses a different scheme, or carries a value without
    /// <paramref name="prefix"/> — all of which the caller must treat as 401 without touching the
    /// database.
    /// </summary>
    public static string? ExtractBearer(string? headerValue, string prefix) {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        if (!headerValue.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase)) return null;
        var token = headerValue[BearerScheme.Length..].Trim();
        return token.StartsWith(prefix, StringComparison.Ordinal) ? token : null;
    }

    /// <summary>
    /// The bearer value from an <c>Authorization</c> header, whatever shape it has. For the tokens that
    /// predate the prefix convention and are whatever an operator typed —
    /// <see cref="Entities.Stack.WebhookToken"/> — where there is no prefix to recognize, so the stored
    /// value is the only thing that can decide.
    /// </summary>
    public static string? ExtractBearer(string? headerValue) {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        if (!headerValue.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase)) return null;
        var token = headerValue[BearerScheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// Compares a presented token against a stored one without an early exit on the first differing
    /// byte. A null or empty stored value always fails, so "no token issued" can never be matched.
    /// </summary>
    public static bool Verify(string? presented, string? stored) {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(stored));
    }
}

/// <summary>
/// The per-product release webhook token (ADR-0026): what a CI workflow presents to
/// <c>POST /api/webhooks/products/{id}/release</c>.
/// </summary>
/// <remarks>
/// Stored in plaintext on the product row for the reasons given on
/// <see cref="Entities.Product.ReleaseWebhookToken"/> — it has to be readable back to be shown for
/// copying and, from the secret-sync stage on, pushed into the repository's Actions secrets.
/// </remarks>
public static class ReleaseWebhookTokens {
    /// <summary>Prefix carried by every release webhook token, so a leaked value is recognizable.</summary>
    public const string Prefix = "wtrel_";

    /// <summary>The Actions secret the workflow snippet reads the token from.</summary>
    public const string SecretName = "WATCHTOWER_RELEASE_TOKEN";

    /// <summary>Creates a fresh token, e.g. <c>wtrel_3q2-7v…</c>.</summary>
    public static string Generate() => BearerTokens.Generate(Prefix);

    /// <summary>The token from an <c>Authorization</c> header, or null when there is no usable one.</summary>
    public static string? ExtractBearer(string? headerValue) =>
        BearerTokens.ExtractBearer(headerValue, Prefix);

    /// <summary>Constant-time comparison of a presented token against the product's stored one.</summary>
    public static bool Verify(string? presented, string? stored) => BearerTokens.Verify(presented, stored);
}
