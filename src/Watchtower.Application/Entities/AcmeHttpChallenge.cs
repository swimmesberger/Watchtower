namespace Watchtower.Application.Entities;

/// <summary>
/// One live HTTP-01 challenge answer — ADR-0024 decision 4. A row rather than process state because the
/// CA's validation request lands on whichever instance answers port 80, which is not necessarily the one
/// that published the token.
/// </summary>
/// <remarks>
/// The token is the primary key, so answering a challenge is one indexed lookup on a path that is only
/// reached for <c>/.well-known/acme-challenge/{token}</c>. <see cref="ExpiresAt"/> exists because the
/// publishing instance can die mid-order: the issuance path deletes its own row on the way out, and the
/// certificate manager's pass sweeps whatever an interrupted order left behind.
/// </remarks>
public sealed class AcmeHttpChallenge {
    /// <summary>The token the CA will fetch. Case-sensitive base64url the CA chose; the primary key.</summary>
    public required string Token { get; set; }

    /// <summary>The exact body the CA compares against (RFC 8555 §8.3).</summary>
    public required string KeyAuthorization { get; set; }

    /// <summary>The host the challenge was published for. Diagnostics only — the answer is host-agnostic.</summary>
    public required string Host { get; set; }

    /// <summary>When this row stops being an answer, whether or not its order ever settled.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
