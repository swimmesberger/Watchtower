using System.Globalization;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;

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
    /// Environment variable carrying the <c>aud</c> value(s) an identity assertion reaching this stack
    /// will carry, resolved from the active edge and the stack's own protected routes
    /// (<see cref="ResolveAudience"/>): the Cloudflare Access applications' AUD tags on the cloudflare
    /// provider, the routes' own hostnames under integrated auth. The other half of
    /// <see cref="JwksUrlVariable"/> — the JWKS says the assertion is genuine, this says it was minted
    /// for <em>this</em> application rather than for some other one behind the same edge.
    /// </summary>
    /// <remarks>
    /// Comma-separated when the stack has more than one protected route, because each of them is a
    /// separate application at the edge and any of their assertions is legitimately this stack's. Every
    /// JWT library takes a set of acceptable audiences, so a reader splits on <c>,</c> and passes the
    /// result straight through.
    /// </remarks>
    public const string AudienceVariable = "WATCHTOWER_AUTH_AUDIENCE";

    /// <summary>
    /// Names Watchtower reserves for itself. An operator-defined stack variable using one of these
    /// keys is skipped at deploy time so the injected value always wins.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal) {
        TokenVariable, StackIdVariable, BaseUrlVariable, JwksUrlVariable, AudienceVariable,
    };

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

    /// <summary>
    /// One of a stack's routes, reduced to what the audience depends on. A projection rather than the
    /// entity so the callers can <c>Select</c> three columns and the resolver stays a pure function the
    /// tests drive directly.
    /// </summary>
    /// <param name="Domain">The route's hostname, or null for a port route (which is always Public).</param>
    /// <param name="AccessMode">Whether the route is gated at all.</param>
    /// <param name="AccessAud">The Cloudflare Access application's AUD tag, when one has been recorded.</param>
    public readonly record struct RouteAudience(string? Domain, AccessMode AccessMode, string? AccessAud);

    /// <summary>
    /// The <c>aud</c> value(s) an assertion reaching this stack will carry, comma-separated and ordered
    /// by hostname, or null when nothing gated in front of it mints one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two edges answer the same question with different values. Cloudflare Access mints an
    /// assertion whose <c>aud</c> is the <em>application's</em> AUD tag, an opaque identifier that only
    /// the edge can produce — so the value has to come from <see cref="RouteAudience.AccessAud"/>, which
    /// the provider recorded when it reconciled the app. Watchtower's own signer mints one whose
    /// <c>aud</c> is the route's hostname (<c>AuthTokenSigner.Mint</c>), so under integrated auth the
    /// answer is already in the routes table and nothing extra is stored.
    /// </para>
    /// <para>
    /// Only protected routes contribute: a <see cref="AccessMode.Public"/> route has no gate in front of
    /// it and therefore no assertion to bind. A protected Cloudflare route whose AUD is not recorded yet
    /// contributes nothing rather than a blank — the app will reject assertions until the next reconcile
    /// fills it in, which is the fail-closed direction and is visible on the Routes page.
    /// </para>
    /// <para>
    /// Several values are not a weakening. Each one names an application of <em>this</em> stack, so the
    /// check still refuses every assertion minted for anything else behind the same edge, which is the
    /// whole point of verifying <c>aud</c> at all.
    /// </para>
    /// </remarks>
    /// <param name="options">Current settings, which decide which edge is issuing.</param>
    /// <param name="routes">The stack's routes.</param>
    /// <returns>The comma-separated audience list, or null when there is nothing to inject.</returns>
    public static string? ResolveAudience(WatchtowerOptions options, IEnumerable<RouteAudience> routes) {
        ArgumentNullException.ThrowIfNull(routes);
        var proxy = options.Proxy;
        var cloudflare = proxy.Enabled && proxy.ResolveProvider() == ProxyProviderKind.Cloudflare;
        // Integrated auth has to actually be on for Watchtower to be minting anything; under Cloudflare
        // the edge mints regardless of Watchtower's own auth setting.
        if (!cloudflare && !options.Auth.Enabled) return null;
        var values = routes
            .Where(r => r.AccessMode != AccessMode.Public && !string.IsNullOrWhiteSpace(r.Domain))
            .OrderBy(r => r.Domain, StringComparer.Ordinal)
            .Select(r => cloudflare ? r.AccessAud?.Trim() : r.Domain!.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0 ? null : string.Join(',', values);
    }

    /// <summary>
    /// Creates a new token: the <see cref="Prefix"/> followed by 32 cryptographically random bytes
    /// encoded as unpadded base64url, so the value is safe in headers, URLs and <c>.env</c> files.
    /// </summary>
    /// <returns>A fresh token, e.g. <c>wtapp_3q2-7v…</c>.</returns>
    public static string Generate() => BearerTokens.Generate(Prefix);

    /// <summary>
    /// Extracts the token from an <c>Authorization</c> header value. Returns null when the header is
    /// absent, uses a different scheme, or does not carry a value with the App API token shape — all
    /// of which the caller must treat as 401 without ever touching the database.
    /// </summary>
    /// <param name="headerValue">Raw <c>Authorization</c> header value.</param>
    /// <returns>The bearer token, or null when the header is missing or malformed.</returns>
    public static string? ExtractBearer(string? headerValue) =>
        BearerTokens.ExtractBearer(headerValue, Prefix);

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
    public static bool Verify(string presented, string? stored) => BearerTokens.Verify(presented, stored);

    /// <summary>
    /// One reserved variable a deploy writes.
    /// </summary>
    /// <param name="Name">The variable name, one of the constants above.</param>
    /// <param name="Value">Its resolved value.</param>
    /// <param name="Secret">
    /// Whether the value is a credential rather than an identifier. True only for
    /// <see cref="TokenVariable"/>: the rest name, locate or scope this stack and are safe to show.
    /// Callers that render the list use this to decide what to mask, and callers that log it use it to
    /// decide what to omit — the deploy log prints names only, whatever this says.
    /// </param>
    public sealed record InjectedVariable(string Name, string Value, bool Secret = false);

    /// <summary>
    /// Exactly what a deploy of this stack writes into its environment, in write order — the one
    /// definition of that, read both by the deploy that performs it and by the API that previews it.
    /// <see cref="BaseUrlVariable"/> is present only when a public base URL is configured,
    /// <see cref="JwksUrlVariable"/> only when an edge is issuing assertions
    /// (<see cref="ResolveJwksUrl"/>), and <see cref="AudienceVariable"/> only when this stack has a
    /// protected route whose audience is known (<see cref="ResolveAudience"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="TokenVariable"/> is listed for the stack, but at deploy time it reaches only the
    /// services the label rules choose (<c>EnvInjectionPlan</c>) rather than all of them. That
    /// distinction needs the resolved compose project, which no settings query has, so a caller
    /// previewing this list should say so rather than imply every service receives it.
    /// </remarks>
    /// <param name="options">Current settings.</param>
    /// <param name="stackId">The stack the preview or the deploy is for.</param>
    /// <param name="appApiToken">The stack's App API bearer token.</param>
    /// <param name="routes">
    /// The stack's routes. Empty answers for a stack that has none, which is also what a caller that
    /// cannot cheaply look them up should pass — naming a variable that will not be written is the
    /// worse error of the two.
    /// </param>
    /// <returns>The reserved variables, in write order.</returns>
    public static IReadOnlyList<InjectedVariable> InjectedVariables(
        WatchtowerOptions options, int stackId, string appApiToken, IEnumerable<RouteAudience> routes) {
        var vars = new List<InjectedVariable> {
            new(TokenVariable, appApiToken, Secret: true),
            new(StackIdVariable, stackId.ToString(CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
            vars.Add(new InjectedVariable(BaseUrlVariable, options.PublicBaseUrl.Trim()));
        if (ResolveJwksUrl(options) is { } jwksUrl)
            vars.Add(new InjectedVariable(JwksUrlVariable, jwksUrl));
        if (ResolveAudience(options, routes) is { } audience)
            vars.Add(new InjectedVariable(AudienceVariable, audience));
        return vars;
    }
}
