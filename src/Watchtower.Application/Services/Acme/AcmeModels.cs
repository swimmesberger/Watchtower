using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watchtower.Application.Services.Acme;

// The wire types of RFC 8555, and only the fields Watchtower actually reads or writes. A CA is free to
// send more (Let's Encrypt does), and STJ ignores what is not declared — which is the property that
// makes a hand-written client viable at all: the protocol's extension points cost nothing here.

/// <summary>
/// The directory resource (RFC 8555 §7.1) — the one URL an operator configures, from which every other
/// endpoint is discovered. Nothing else in this client hard-codes a CA's URL layout.
/// </summary>
public sealed record AcmeDirectory {
    [JsonPropertyName("newNonce")] public string NewNonce { get; init; } = "";
    [JsonPropertyName("newAccount")] public string NewAccount { get; init; } = "";
    [JsonPropertyName("newOrder")] public string NewOrder { get; init; } = "";
    [JsonPropertyName("revokeCert")] public string? RevokeCert { get; init; }
    [JsonPropertyName("keyChange")] public string? KeyChange { get; init; }
    [JsonPropertyName("meta")] public AcmeDirectoryMeta? Meta { get; init; }
}

/// <summary>
/// The directory's <c>meta</c> object (RFC 8555 §7.1.1). <see cref="ExternalAccountRequired"/> is the
/// field worth acting on: a CA that sets it will refuse every account registration that carries no EAB,
/// and saying so up front beats a <c>externalAccountRequired</c> problem document weeks later.
/// </summary>
public sealed record AcmeDirectoryMeta {
    [JsonPropertyName("termsOfService")] public string? TermsOfService { get; init; }
    [JsonPropertyName("website")] public string? Website { get; init; }
    [JsonPropertyName("caaIdentities")] public string[]? CaaIdentities { get; init; }
    [JsonPropertyName("externalAccountRequired")] public bool? ExternalAccountRequired { get; init; }
}

/// <summary>The account resource (RFC 8555 §7.1.2). The account URL itself is the <c>Location</c> header,
/// not a field, which is why registration returns it separately.</summary>
public sealed record AcmeAccount {
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("contact")] public string[]? Contact { get; init; }
    [JsonPropertyName("orders")] public string? Orders { get; init; }
}

/// <summary>One identifier an order covers. Watchtower only ever sends <c>type: "dns"</c>.</summary>
public sealed record AcmeIdentifier {
    [JsonPropertyName("type")] public string Type { get; init; } = "dns";
    [JsonPropertyName("value")] public string Value { get; init; } = "";
}

/// <summary>
/// The order resource (RFC 8555 §7.1.3). <see cref="Status"/> walks
/// <c>pending → ready → processing → valid</c>, or lands on <c>invalid</c>; the client polls it rather
/// than assuming, because a CA may finalize synchronously or asynchronously and both are legal.
/// </summary>
public sealed record AcmeOrder {
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("expires")] public DateTimeOffset? Expires { get; init; }
    [JsonPropertyName("identifiers")] public AcmeIdentifier[] Identifiers { get; init; } = [];
    [JsonPropertyName("authorizations")] public string[] Authorizations { get; init; } = [];
    [JsonPropertyName("finalize")] public string Finalize { get; init; } = "";
    [JsonPropertyName("certificate")] public string? Certificate { get; init; }
    [JsonPropertyName("error")] public AcmeProblem? Error { get; init; }
}

/// <summary>
/// The authorization resource (RFC 8555 §7.1.4): one identifier and the challenges the CA will accept
/// as proof of control over it.
/// </summary>
public sealed record AcmeAuthorization {
    [JsonPropertyName("identifier")] public AcmeIdentifier Identifier { get; init; } = new();
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("expires")] public DateTimeOffset? Expires { get; init; }
    [JsonPropertyName("challenges")] public AcmeChallenge[] Challenges { get; init; } = [];
    [JsonPropertyName("wildcard")] public bool? Wildcard { get; init; }
}

/// <summary>
/// One challenge (RFC 8555 §8). Watchtower answers <c>http-01</c> only — see ADR-0022: TLS-ALPN-01
/// cannot be implemented on Kestrel, whose <c>SslClientHelloInfo</c> exposes no ALPN protocol list, and
/// DNS-01 would need write credentials for every operator's zone.
/// </summary>
public sealed record AcmeChallenge {
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("validated")] public DateTimeOffset? Validated { get; init; }
    [JsonPropertyName("error")] public AcmeProblem? Error { get; init; }
}

/// <summary>
/// An RFC 7807 problem document as ACME profiles it (RFC 8555 §6.7). <see cref="Type"/> is the URN the
/// client branches on; <see cref="Detail"/> is the sentence an operator is shown.
/// </summary>
public sealed record AcmeProblem {
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("status")] public int? Status { get; init; }
    [JsonPropertyName("subproblems")] public AcmeSubproblem[]? Subproblems { get; init; }
}

/// <summary>
/// One identifier-scoped problem inside a problem document. Watchtower orders one identifier at a time,
/// so at most one of these ever arrives — but the CA's per-identifier detail is more specific than the
/// envelope's, so it is worth reading.
/// </summary>
public sealed record AcmeSubproblem {
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("identifier")] public AcmeIdentifier? Identifier { get; init; }
}

/// <summary>The <c>newAccount</c> request body (RFC 8555 §7.3).</summary>
public sealed record NewAccountPayload {
    [JsonPropertyName("termsOfServiceAgreed")] public bool TermsOfServiceAgreed { get; init; } = true;

    /// <summary>
    /// <c>mailto:</c> URIs. Omitted entirely — not sent as an empty array — when no admin email is
    /// configured: some CAs reject <c>[]</c> as a malformed contact list.
    /// </summary>
    [JsonPropertyName("contact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Contact { get; init; }

    /// <summary>
    /// The External Account Binding, itself a complete JWS object (RFC 8555 §7.3.4). Carried as raw JSON
    /// because it is produced by <see cref="AcmeJws.ExternalAccountBinding"/> as a signed string and must
    /// reach the CA byte-for-byte — re-modelling it as records would risk a re-serialization that no
    /// longer matches the signature it contains.
    /// </summary>
    [JsonPropertyName("externalAccountBinding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ExternalAccountBinding { get; init; }
}

/// <summary>The <c>newOrder</c> request body (RFC 8555 §7.4).</summary>
public sealed record NewOrderPayload {
    [JsonPropertyName("identifiers")] public AcmeIdentifier[] Identifiers { get; init; } = [];
}

/// <summary>The finalize request body (RFC 8555 §7.4): the CSR, DER-encoded and base64url'd.</summary>
public sealed record FinalizePayload {
    [JsonPropertyName("csr")] public string Csr { get; init; } = "";
}

/// <summary>What <c>account.json</c> holds next to the account key.</summary>
/// <remarks>
/// The directory URL is stored alongside the account URL so a mismatch is detectable: an account is
/// meaningful only to the CA that issued it, and reusing one against a different directory produces
/// <c>accountDoesNotExist</c> on every request. The directory also keys the account <em>folder</em>, so
/// this is belt and braces — but a folder can be copied between deployments, and the field is what makes
/// that visible.
/// </remarks>
public sealed record AcmeAccountFile {
    [JsonPropertyName("directoryUrl")] public string DirectoryUrl { get; init; } = "";
    [JsonPropertyName("accountUrl")] public string? AccountUrl { get; init; }
}

/// <summary>
/// The error URNs this client branches on (RFC 8555 §6.7, and the IANA ACME error registry). Only the
/// ones that change behaviour are named — everything else is treated as a transport failure and retried
/// on the backoff ladder.
/// </summary>
public static class AcmeProblemTypes {
    private const string Prefix = "urn:ietf:params:acme:error:";

    /// <summary>The nonce was stale. Never surfaces to a caller — the client re-signs and retries.</summary>
    public const string BadNonce = Prefix + "badNonce";

    /// <summary>The CA's rate limit. Carries a <c>Retry-After</c> worth obeying to the second.</summary>
    public const string RateLimited = Prefix + "rateLimited";

    /// <summary>The CA could not connect to the challenge responder — DNS or firewall, on the operator's side.</summary>
    public const string Connection = Prefix + "connection";

    /// <summary>The CA could not resolve the identifier.</summary>
    public const string Dns = Prefix + "dns";

    /// <summary>The challenge response did not match — the responder answered, with the wrong thing.</summary>
    public const string Unauthorized = Prefix + "unauthorized";

    /// <summary>The responder answered something unexpected (a login page, a redirect, an error).</summary>
    public const string IncorrectResponse = Prefix + "incorrectResponse";

    /// <summary>The operator must do something — accept new terms, most often. Retrying cannot help.</summary>
    public const string UserActionRequired = Prefix + "userActionRequired";

    /// <summary>The stored account URL is unknown to the CA; the client re-registers once.</summary>
    public const string AccountDoesNotExist = Prefix + "accountDoesNotExist";

    /// <summary>Watchtower sent something the CA would not parse — a bug here, not a transient failure.</summary>
    public const string Malformed = Prefix + "malformed";

    /// <summary>The CA does not issue for this kind of identifier.</summary>
    public const string UnsupportedIdentifier = Prefix + "unsupportedIdentifier";

    /// <summary>The CA will not issue for this name (blocked, high-risk, or not on its allow-list).</summary>
    public const string RejectedIdentifier = Prefix + "rejectedIdentifier";

    /// <summary>The CA requires an External Account Binding this client did not send.</summary>
    public const string ExternalAccountRequired = Prefix + "externalAccountRequired";
}

/// <summary>
/// An ACME request that did not succeed. Carries the CA's own problem document where there was one, so
/// the operator-facing detail is the CA's sentence rather than a status code this client invented.
/// </summary>
public sealed class AcmeException(
    AcmeProblem? problem, HttpStatusCode status, TimeSpan? retryAfter, string message)
    : Exception(message) {
    /// <summary>The parsed <c>application/problem+json</c> body, when the response carried one.</summary>
    public AcmeProblem? Problem { get; } = problem;

    /// <summary>The HTTP status the CA answered with.</summary>
    public HttpStatusCode Status { get; } = status;

    /// <summary>The <c>Retry-After</c> the CA asked for, when it sent one. Obeyed by the backoff ladder.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>Whether the problem carries exactly <paramref name="urn"/> as its type.</summary>
    public bool IsType(string urn) =>
        Problem?.Type is { } type && string.Equals(type, urn, StringComparison.Ordinal);
}
