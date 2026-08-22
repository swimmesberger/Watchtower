using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// The tokens an in-flight ACME HTTP-01 challenge expects to be answered on
/// <c>/.well-known/acme-challenge/{token}</c> — ADR-0017 (forthcoming). The certificate manager publishes
/// one before it tells the CA to validate and drops it when the order settles; the challenge middleware
/// (the only reader) answers from here without touching a database or a disk.
/// </summary>
/// <remarks>
/// Deliberately process state and nothing more. A challenge is live for seconds, is meaningful only to the
/// CA that is about to call back, and is worthless after the order settles, so persisting it would create
/// a store whose stale rows are the only thing anyone would ever find in it. The single-node design
/// (ADR-0001) is what makes an in-memory answer sufficient: the process that publishes the token is the
/// process the CA reaches.
/// <para>
/// Both halves are safe to call from anywhere: the writer runs on the issuance path and the reader on
/// every request to that prefix, on any host and over either scheme.
/// </para>
/// </remarks>
public sealed class AcmeHttpChallengeStore {
    /// <summary>
    /// Token ⇒ key authorization. Ordinal keys: the token is a base64url string the CA chose, and case is
    /// significant in it — matching it case-insensitively would answer a challenge that was never issued.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _published = new(StringComparer.Ordinal);

    /// <summary>
    /// Publishes <paramref name="keyAuthorization"/> under <paramref name="token"/> until the returned
    /// handle is disposed. Scoped rather than published-and-forgotten so a failed order cannot leave a
    /// token answerable: the caller's <c>using</c> is what retracts it, on the success and the throw path
    /// alike.
    /// </summary>
    public IDisposable Publish(string token, string keyAuthorization) {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyAuthorization);
        _published[token] = keyAuthorization;
        return new Publication(this, token);
    }

    /// <summary>
    /// The key authorization to answer <paramref name="token"/> with, or <see langword="false"/> when no
    /// challenge is live under that token.
    /// </summary>
    public bool TryGet(string token, [NotNullWhen(true)] out string? keyAuthorization) =>
        _published.TryGetValue(token, out keyAuthorization);

    /// <summary>How many challenges are currently answerable. For diagnostics and tests.</summary>
    public int Count => _published.Count;

    /// <summary>
    /// One publication's lifetime. Disposal is idempotent, so a caller that disposes twice — or a
    /// <c>using</c> unwinding over an already-retracted token — is not an error.
    /// </summary>
    private sealed class Publication(AcmeHttpChallengeStore store, string token) : IDisposable {
        public void Dispose() => store._published.TryRemove(token, out _);
    }
}
