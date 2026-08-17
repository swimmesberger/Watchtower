using System.Security.Cryptography;
using System.Text;
using Watchtower.Application.Config;

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
    /// Environment variable carrying the JWKS URL an app should verify its identity assertion
    /// against, resolved from the active edge (<see cref="ResolveJwksUrl"/>): Cloudflare Access's
    /// team certs URL on the cloudflare provider, Watchtower's own <c>/api/auth/jwks</c> under
    /// integrated auth. Apps that read this instead of hard-coding an issuer swap edges with zero
    /// configuration — the next deploy re-injects the right URL.
    /// </summary>
    public const string JwksUrlVariable = "WATCHTOWER_AUTH_JWKS_URL";

    /// <summary>
    /// Names Watchtower reserves for itself. An operator-defined stack variable using one of these
    /// keys is skipped at deploy time so the injected value always wins.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(StringComparer.Ordinal) { TokenVariable, StackIdVariable, BaseUrlVariable, JwksUrlVariable };

    /// <summary>
    /// The JWKS URL for the identity assertions apps behind the active edge will see, or null when
    /// no edge is issuing any: the Cloudflare Access certs URL
    /// (<c>https://{team}.cloudflareaccess.com/cdn-cgi/access/certs</c>, requiring
    /// <c>Proxy:Cloudflare:TeamDomain</c>) when the cloudflare provider is active, else Watchtower's
    /// own <c>{PublicBaseUrl}/api/auth/jwks</c> when integrated auth is enabled and a public base URL
    /// is configured.
    /// </summary>
    public static string? ResolveJwksUrl(WatchtowerOptions options) {
        var proxy = options.Proxy;
        if (proxy.Enabled && proxy.ResolveProvider() == ProxyProviderKind.Cloudflare) {
            var team = proxy.Cloudflare.TeamDomain?.Trim();
            if (string.IsNullOrWhiteSpace(team)) return null;
            // Accept the bare team name or the full host, however the operator wrote it down.
            var host = team.Contains('.') ? team : $"{team}.cloudflareaccess.com";
            return $"https://{host}/cdn-cgi/access/certs";
        }
        if (options.Auth.Enabled && !string.IsNullOrWhiteSpace(options.PublicBaseUrl))
            return $"{options.PublicBaseUrl.TrimEnd('/')}/api/auth/jwks";
        return null;
    }

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
    /// Compares a presented token against a stored one without an early-exit on the first differing
    /// byte.
    /// </summary>
    /// <remarks>
    /// This is a defense-in-depth re-check of a row that an indexed SQL equality predicate already
    /// selected; that predicate is the deciding comparison and is not constant-time, so this call
    /// does not by itself make authentication timing-safe. It exists to re-assert the match in
    /// process — catching, for example, a store collation that compares more loosely than intended.
    /// </remarks>
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
    /// present when a public base URL is configured, <see cref="JwksUrlVariable"/> only when an edge
    /// is issuing assertions (<see cref="ResolveJwksUrl"/>).
    /// </summary>
    /// <returns>The reserved variable names a deploy of this stack will write.</returns>
    public static IReadOnlyList<string> InjectedVariableNames(WatchtowerOptions options) {
        var names = new List<string> { TokenVariable, StackIdVariable };
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)) names.Add(BaseUrlVariable);
        if (ResolveJwksUrl(options) is not null) names.Add(JwksUrlVariable);
        return names;
    }
}
