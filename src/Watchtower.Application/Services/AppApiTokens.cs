using System.Security.Cryptography;
using System.Text;

namespace Watchtower.Application.Services;

/// <summary>
/// Generation and constant-time verification of the per-stack App API bearer token, plus the reserved
/// environment-variable names Watchtower injects into every deploy.
/// </summary>
/// <remarks>
/// Tokens are stored in plaintext on the <c>stacks</c> row. This is deliberate and consistent with
/// <c>Stack.WebhookToken</c> and <c>Credential.Token</c>: the token has to be re-injected into the
/// stack's environment on every single deploy, so Watchtower must be able to read it back. A hash
/// would make the value unrecoverable and force a rotation on each deploy. The token is never written
/// to logs or to deploy output.
/// </remarks>
public static class AppApiTokens {
    /// <summary>Prefix carried by every App API token; lets callers and logs recognize the token shape.</summary>
    public const string Prefix = "wtapp_";

    /// <summary>Environment variable carrying the stack's App API bearer token.</summary>
    public const string TokenVariable = "WATCHTOWER_APP_TOKEN";

    /// <summary>Environment variable carrying the stack's numeric Watchtower id.</summary>
    public const string StackIdVariable = "WATCHTOWER_STACK_ID";

    /// <summary>
    /// Environment variable carrying Watchtower's publicly reachable base URL. Only injected when
    /// <c>Watchtower:PublicBaseUrl</c> is configured.
    /// </summary>
    public const string BaseUrlVariable = "WATCHTOWER_URL";

    /// <summary>
    /// Names Watchtower reserves for itself. An operator-defined stack variable using one of these
    /// keys is skipped at deploy time so the injected value always wins.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(StringComparer.Ordinal) { TokenVariable, StackIdVariable, BaseUrlVariable };

    /// <summary>Number of random bytes behind each token (256 bits).</summary>
    private const int EntropyBytes = 32;

    /// <summary>
    /// Creates a new token: the <see cref="Prefix"/> followed by 32 cryptographically random bytes
    /// encoded as unpadded base64url, so the value is safe in headers, URLs and <c>.env</c> files.
    /// </summary>
    /// <returns>A fresh token, e.g. <c>wtapp_3q2-7v…</c>.</returns>
    public static string Generate() {
        var bytes = RandomNumberGenerator.GetBytes(EntropyBytes);
        var encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Prefix + encoded;
    }

    /// <summary>
    /// Extracts the token from an <c>Authorization</c> header value. Returns null when the header is
    /// absent, uses a different scheme, or does not carry a value with the App API token shape — all
    /// of which the caller must treat as 401 without ever touching the database.
    /// </summary>
    /// <param name="headerValue">Raw <c>Authorization</c> header value.</param>
    /// <returns>The bearer token, or null when the header is missing or malformed.</returns>
    public static string? ExtractBearer(string? headerValue) {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        const string scheme = "Bearer ";
        if (!headerValue.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var token = headerValue[scheme.Length..].Trim();
        return token.StartsWith(Prefix, StringComparison.Ordinal) ? token : null;
    }

    /// <summary>
    /// Compares a presented token against a stored one in constant time. Used as a second gate after
    /// the indexed lookup so the match itself is never decided by an early-exit string comparison.
    /// </summary>
    /// <param name="presented">Token supplied by the caller.</param>
    /// <param name="stored">Token persisted on the stack row; null/empty always fails.</param>
    /// <returns>True only when both are non-empty and byte-identical.</returns>
    public static bool Verify(string presented, string? stored) {
        if (string.IsNullOrEmpty(stored)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(stored));
    }

    /// <summary>
    /// Names actually injected into a deploy, in write order. <see cref="BaseUrlVariable"/> is only
    /// present when a public base URL is configured.
    /// </summary>
    /// <param name="publicBaseUrl">Configured <c>Watchtower:PublicBaseUrl</c>, or null when unset.</param>
    /// <returns>The reserved variable names a deploy of this stack will write.</returns>
    public static IReadOnlyList<string> InjectedVariableNames(string? publicBaseUrl) =>
        string.IsNullOrWhiteSpace(publicBaseUrl)
            ? [TokenVariable, StackIdVariable]
            : [TokenVariable, StackIdVariable, BaseUrlVariable];
}
