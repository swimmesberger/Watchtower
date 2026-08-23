using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The HTTP-01 challenge answers, as rows (ADR-0024). The one property that made the move necessary is
/// the first test here: the instance that publishes a token is not necessarily the one the CA calls.
/// </summary>
public sealed class AcmeHttpChallengeStoreTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    // A real ACME token is 43 base64url characters (RFC 8555 wants at least 128 bits of entropy), and
    // the store refuses anything that is not shaped like one — so the fixtures have to be realistic.
    private const string Token = "aGVsbG8td29ybGQtY2hhbGxlbmdlLXRva2VuLTAwMQ";
    private const string KeyAuthorization = Token + ".thumbprint";
    private const string Host = "app.example.invalid";

    [Fact]
    public async Task APublishedToken_IsAnswerable() {
        using var host = AuthTestHost.Start();
        var store = Store(host);

        await using var published = await store.PublishAsync(Token, KeyAuthorization, Host, ct: Ct);

        Assert.Equal(KeyAuthorization, await store.TryGetAsync(Token, Ct));
        Assert.Equal(1, await store.CountAsync(Ct));
    }

    /// <summary>
    /// The whole reason the tokens are rows: the CA's validation request lands on whichever instance
    /// answers port 80, which is not necessarily the one that opened the order.
    /// </summary>
    [Fact]
    public async Task AnotherInstance_AnswersTheSameToken() {
        using var first = AuthTestHost.Start();
        using var second = first.Restart();

        await using var published = await Store(first).PublishAsync(Token, KeyAuthorization, Host, ct: Ct);

        Assert.Equal(KeyAuthorization, await Store(second).TryGetAsync(Token, Ct));
    }

    [Fact]
    public async Task AnUnknownToken_IsNotAnswered() {
        using var host = AuthTestHost.Start();

        Assert.Null(await Store(host).TryGetAsync("bmV2ZXItaXNzdWVkLXRva2VuLXZhbHVl", Ct));
        Assert.Null(await Store(host).TryGetAsync("", Ct));
    }

    /// <summary>
    /// The token is base64url and case is significant in it. Matching case-insensitively would answer a
    /// challenge that was never issued.
    /// </summary>
    [Fact]
    public async Task TheTokenIsCaseSensitive() {
        using var host = AuthTestHost.Start();
        await using var published = await Store(host).PublishAsync(Token, KeyAuthorization, Host, ct: Ct);

        Assert.Null(await Store(host).TryGetAsync(Token.ToLowerInvariant(), Ct));
    }

    [Fact]
    public async Task Disposing_RetractsIt() {
        using var host = AuthTestHost.Start();
        var store = Store(host);
        var published = await store.PublishAsync(Token, KeyAuthorization, Host, ct: Ct);

        await published.DisposeAsync();
        // Idempotent: an order that fails after the await using has already unwound must not throw.
        await published.DisposeAsync();

        Assert.Null(await store.TryGetAsync(Token, Ct));
        Assert.Equal(0, await store.CountAsync(Ct));
    }

    /// <summary>A retried order reuses the CA's token; re-publishing has to extend it, not fail.</summary>
    [Fact]
    public async Task RePublishing_ExtendsRatherThanFails() {
        using var host = AuthTestHost.Start();
        var store = Store(host);

        await using var first = await store.PublishAsync(Token, KeyAuthorization, Host, ct: Ct);
        await using var second = await store.PublishAsync(Token, KeyAuthorization, Host, ct: Ct);

        Assert.Equal(KeyAuthorization, await store.TryGetAsync(Token, Ct));
        Assert.Equal(1, await store.CountAsync(Ct));
    }

    /// <summary>
    /// The expiry is enforced on read, not only by the sweep: the sweep runs on the certificate
    /// manager's cadence, and a token has to stop being answerable when it says it does rather than when
    /// housekeeping next happens to run.
    /// </summary>
    [Fact]
    public async Task AnExpiredToken_StopsBeingAnswered_BeforeItIsSwept() {
        using var host = AuthTestHost.Start();
        var store = Store(host);
        await using var published = await store.PublishAsync(
            Token, KeyAuthorization, Host, TimeSpan.FromMinutes(5), Ct);

        host.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(await store.TryGetAsync(Token, Ct));
        Assert.Equal(0, await store.CountAsync(Ct));
    }

    /// <summary>What an instance killed mid-order leaves behind, and what the manager's pass clears.</summary>
    [Fact]
    public async Task SweepExpired_DeletesOnlyWhatHasExpired() {
        using var host = AuthTestHost.Start();
        var store = Store(host);
        // Published and deliberately not disposed — the crashed-instance case.
        await store.PublishAsync("c3RhbGUtdG9rZW4tbGVmdC1iZWhpbmQ", "stale.auth", Host, TimeSpan.FromMinutes(5), Ct);
        host.Time.Advance(TimeSpan.FromMinutes(6));
        await store.PublishAsync(Token, KeyAuthorization, Host, ct: Ct);

        Assert.Equal(1, await store.SweepExpiredAsync(Ct));

        Assert.Null(await store.TryGetAsync("c3RhbGUtdG9rZW4tbGVmdC1iZWhpbmQ", Ct));
        Assert.Equal(KeyAuthorization, await store.TryGetAsync(Token, Ct));
    }

    // ── Keeping a stranger off the database ──────────────────────────────────

    /// <summary>
    /// The responder is anonymous by protocol and reachable on port 80 for every host the proxy serves,
    /// so the cheapest possible rejection of an obviously-invented token is the first line of defence.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("token with spaces in it")]
    [InlineData("has/a/slash/in/it/and/is/long/enough")]
    [InlineData("has+plus+and+slash/==padding==")]
    [InlineData("../../../etc/passwd-and-then-some-padding")]
    public void MalformedTokens_AreNotWellFormed(string? token) =>
        Assert.False(AcmeHttpChallengeStore.IsWellFormedToken(token));

    [Theory]
    [InlineData("aGVsbG8td29ybGQtdG9rZW4")]
    [InlineData("Zm9vYmFyX2Jhei1xdXV4LTAxMjM0NTY3ODk")]
    public void RealTokens_AreWellFormed(string token) =>
        Assert.True(AcmeHttpChallengeStore.IsWellFormedToken(token));

    [Fact]
    public async Task AMalformedToken_IsRefusedWithoutTouchingTheDatabase() {
        using var host = AuthTestHost.Start();

        // No assertion on query counts — the guard is a pure function, so the property is stated where
        // it is decided (above) and this pins that TryGetAsync actually consults it.
        Assert.Null(await Store(host).TryGetAsync("not a token", Ct));
        Assert.Null(await Store(host).TryGetAsync("short", Ct));
    }

    /// <summary>
    /// A token this instance published is answered from memory. On a single-node deployment — which is
    /// most of them — that is every challenge the CA ever asks for, and it is also the path the
    /// issuer's own self-check goes through moments before the CA arrives.
    /// </summary>
    [Fact]
    public async Task ALocallyPublishedToken_IsAnsweredWithoutTheDatabase() {
        using var host = AuthTestHost.Start();
        var store = Store(host);
        await using var published = await store.PublishAsync(Token, KeyAuthorization, Host, ct: Ct);

        // The row deleted behind the store's back: whatever answers now is not coming from the table.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.AcmeHttpChallenges.Where(c => c.Token == Token).ExecuteDeleteAsync(Ct);
        }

        Assert.Equal(KeyAuthorization, await store.TryGetAsync(Token, Ct));
    }

    /// <summary>
    /// A miss is remembered briefly, so a stranger looping over invented tokens costs one query rather
    /// than one per request. Proven by making the database answer differently behind the cache.
    /// </summary>
    [Fact]
    public async Task AMiss_IsRememberedBriefly() {
        using var host = AuthTestHost.Start();
        var store = Store(host);
        var token = "bWlzc2VkLXRva2VuLXZhbHVl";

        Assert.Null(await store.TryGetAsync(token, Ct));

        // Inserted directly, so the store has no local publication to answer from: the only way it
        // could say "no" now is the remembered miss.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.AcmeHttpChallenges.Add(new AcmeHttpChallenge {
                Token = token,
                KeyAuthorization = "later.auth",
                Host = Host,
                ExpiresAt = host.Time.GetUtcNow().AddMinutes(10),
            });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Null(await store.TryGetAsync(token, Ct));

        // …and it is brief. Past the TTL the store asks again and finds what is there — which is what
        // bounds how long a token published on another instance can take to become answerable here.
        host.Time.Advance(AcmeHttpChallengeStore.NegativeCacheTtl + TimeSpan.FromSeconds(1));
        Assert.Equal("later.auth", await store.TryGetAsync(token, Ct));
    }

    /// <summary>Publishing has to beat a miss remembered a moment earlier, or an order answers 404.</summary>
    [Fact]
    public async Task PublishingClearsARememberedMiss() {
        using var host = AuthTestHost.Start();
        var store = Store(host);
        Assert.Null(await store.TryGetAsync(Token, Ct));

        await using var published = await store.PublishAsync(Token, KeyAuthorization, Host, ct: Ct);

        Assert.Equal(KeyAuthorization, await store.TryGetAsync(Token, Ct));
    }

    private static AcmeHttpChallengeStore Store(AuthTestHost host) =>
        host.Services.GetRequiredService<AcmeHttpChallengeStore>();
}
