using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// The tokens an in-flight ACME HTTP-01 challenge expects to be answered on
/// <c>/.well-known/acme-challenge/{token}</c> — ADR-0022, moved into the database by ADR-0024. The
/// certificate manager publishes one before it tells the CA to validate and drops it when the order
/// settles; the challenge middleware (the only reader) answers from the table.
/// </summary>
/// <remarks>
/// <para>
/// Rows rather than process state, and the reason is the one thing a single node could take for granted:
/// the CA's validation request lands on whichever instance answers port 80, which is not necessarily the
/// one that opened the order. In-memory publication was correct exactly as long as those were the same
/// process.
/// </para>
/// <para>
/// The row carries an expiry because the publishing instance can die mid-order: the <c>await using</c>
/// retracts the token on the success and the throw path alike, and <see cref="SweepExpiredAsync"/> in
/// the manager's pass clears whatever an interrupted order left behind.
/// </para>
/// <para>
/// <b>Three guards stand between the open internet and the database</b>, because the responder is
/// anonymous by protocol and reachable on port 80 for every host the proxy serves — so a stranger
/// looping over made-up tokens is a query generator unless something stops them.
/// <see cref="IsWellFormedToken"/> rejects anything that is not shaped like an ACME token before a
/// scope is opened; a token this instance published itself is answered from memory without a query at
/// all; and a miss is remembered briefly, so the same made-up token asked for a thousand times costs
/// one query rather than a thousand. The negative cache is the reason a token published on
/// <em>another</em> instance can take up to <see cref="NegativeCacheTtl"/> to become answerable here —
/// which is well inside the CA's own retry cadence, and the instance that published it answers
/// immediately.
/// </para>
/// </remarks>
public sealed class AcmeHttpChallengeStore(
    IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<AcmeHttpChallengeStore> logger) {
    /// <summary>
    /// How long a published token stays answerable. Comfortably longer than the issuer's
    /// two-minute authorization timeout, so an order never has its own answer swept out from under it,
    /// and short enough that an abandoned token is not answerable for the rest of the day.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    /// <summary>How long a miss is remembered, so a stranger's loop costs one query rather than many.</summary>
    public static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many misses are remembered before the whole set is dropped. A flat cap and a wholesale clear
    /// rather than an eviction policy: the cache exists to blunt a flood, and the cost of forgetting it
    /// entirely is one query per live challenge — which is what the code did before it existed.
    /// </summary>
    private const int NegativeCacheCapacity = 256;

    /// <summary>The shortest and longest a base64url ACME token may plausibly be.</summary>
    private const int MinimumTokenLength = 16;
    private const int MaximumTokenLength = 128;

    /// <summary>
    /// Tokens this instance published, answered without a query. The publishing instance is the one the
    /// issuer's own self-check goes through, and — on a single-node deployment, which is most of them —
    /// the one the CA reaches too.
    /// </summary>
    private readonly ConcurrentDictionary<string, Published> _local = new(StringComparer.Ordinal);

    /// <summary>Token ⇒ when this miss stops being remembered.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _misses = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="token"/> is shaped like an ACME token at all: RFC 8555 §8.3 requires a
    /// base64url string with at least 128 bits of entropy, so anything outside that alphabet or wildly
    /// outside that length was never issued by any CA and is not worth asking the database about.
    /// </summary>
    public static bool IsWellFormedToken(string? token) {
        if (token is null) return false;
        if (token.Length is < MinimumTokenLength or > MaximumTokenLength) return false;
        foreach (var c in token)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        return true;
    }

    /// <summary>
    /// Publishes <paramref name="keyAuthorization"/> under <paramref name="token"/> until the returned
    /// handle is disposed. Scoped rather than published-and-forgotten so a failed order cannot leave a
    /// token answerable: the caller's <c>await using</c> is what retracts it, on the success and the
    /// throw path alike.
    /// </summary>
    public async Task<IAsyncDisposable> PublishAsync(
        string token, string keyAuthorization, string host, TimeSpan? ttl = null,
        CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyAuthorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var expiresAt = time.GetUtcNow() + (ttl ?? DefaultTtl);
        // Ahead of the write, both of them: a token that is about to be answerable must not be refused
        // by a miss this instance remembered a moment ago, and the local answer should be live before
        // the issuer's self-check goes looking for it.
        _misses.TryRemove(token, out _);
        _local[token] = new Published(keyAuthorization, expiresAt);

        await using (var scope = scopeFactory.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var existing = await db.AcmeHttpChallenges.FirstOrDefaultAsync(c => c.Token == token, ct);
            if (existing is null) {
                db.AcmeHttpChallenges.Add(new AcmeHttpChallenge {
                    Token = token,
                    KeyAuthorization = keyAuthorization,
                    Host = host,
                    ExpiresAt = expiresAt,
                });
            } else {
                // A retry of the same order: the CA reuses the token, and re-publishing has to extend it
                // rather than fail.
                existing.KeyAuthorization = keyAuthorization;
                existing.Host = host;
                existing.ExpiresAt = expiresAt;
            }

            try {
                await db.SaveChangesAsync(ct);
            } catch (DbUpdateException ex) when (existing is null && IsUniqueViolation(ex)) {
                // Two attempts for the same host raced. The row that landed carries the same key
                // authorization — it is derived from the account key and the token — so it answers this
                // order too.
            }
        }

        return new Publication(this, token);
    }

    /// <summary>
    /// The key authorization to answer <paramref name="token"/> with, or <see langword="null"/> when no
    /// challenge is live under that token.
    /// </summary>
    /// <remarks>
    /// The expiry is enforced on read as well as swept in the background: the sweep runs on the
    /// certificate manager's cadence, and a token must stop being answerable when it says it does rather
    /// than when housekeeping next happens to run.
    /// </remarks>
    public async Task<string?> TryGetAsync(string token, CancellationToken ct = default) {
        // Nothing that is not token-shaped reaches the database, whatever a stranger sends.
        if (!IsWellFormedToken(token)) return null;
        var now = time.GetUtcNow();

        if (_local.TryGetValue(token, out var published)) {
            if (published.ExpiresAt > now) return published.KeyAuthorization;
            _local.TryRemove(token, out _);
        }

        if (_misses.TryGetValue(token, out var rememberedUntil)) {
            if (rememberedUntil > now) return null;
            _misses.TryRemove(token, out _);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var answer = await db.AcmeHttpChallenges.AsNoTracking()
            .Where(c => c.Token == token && c.ExpiresAt > now)
            .Select(c => c.KeyAuthorization)
            .FirstOrDefaultAsync(ct);
        if (answer is null) RememberMiss(token, now);
        return answer;
    }

    /// <summary>
    /// Remembers that <paramref name="token"/> is not answerable, briefly. Cleared wholesale at the cap
    /// rather than evicted one by one — see <see cref="NegativeCacheCapacity"/>.
    /// </summary>
    private void RememberMiss(string token, DateTimeOffset now) {
        if (_misses.Count >= NegativeCacheCapacity) _misses.Clear();
        _misses[token] = now + NegativeCacheTtl;
    }

    /// <summary>How many challenges are currently answerable. For diagnostics and tests.</summary>
    public async Task<int> CountAsync(CancellationToken ct = default) {
        var now = time.GetUtcNow();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AcmeHttpChallenges.CountAsync(c => c.ExpiresAt > now, ct);
    }

    /// <summary>
    /// Deletes rows past their expiry — what an order abandoned by a crashed instance leaves behind.
    /// Called from the certificate manager's pass, which is the loop that already runs on the right
    /// cadence and would otherwise need a second one invented for it.
    /// </summary>
    /// <returns>How many rows were removed.</returns>
    public async Task<int> SweepExpiredAsync(CancellationToken ct = default) {
        var now = time.GetUtcNow();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AcmeHttpChallenges.Where(c => c.ExpiresAt <= now).ExecuteDeleteAsync(ct);
    }

    private async Task RetractAsync(string token) {
        _local.TryRemove(token, out _);
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.AcmeHttpChallenges.Where(c => c.Token == token).ExecuteDeleteAsync(CancellationToken.None);
        } catch (Exception ex) {
            // Not fatal: the row expires on its own, and until then it answers with a key authorization
            // the CA was told anyway. Worth a line, because a retraction that keeps failing means the
            // database is unhappy in a way the next order will hit harder.
            logger.LogWarning(ex, "Could not retract the ACME challenge token after the order settled.");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>One token this instance published, with the expiry it was published under.</summary>
    private readonly record struct Published(string KeyAuthorization, DateTimeOffset ExpiresAt);

    /// <summary>
    /// One publication's lifetime. Disposal is idempotent, so a caller that disposes twice — or an
    /// <c>await using</c> unwinding over an already-retracted token — is not an error.
    /// </summary>
    private sealed class Publication(AcmeHttpChallengeStore store, string token) : IAsyncDisposable {
        private int _disposed;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0
                ? new ValueTask(store.RetractAsync(token))
                : ValueTask.CompletedTask;
    }
}
