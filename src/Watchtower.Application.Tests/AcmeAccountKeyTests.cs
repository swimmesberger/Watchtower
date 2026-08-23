using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The ACME account in the database (ADR-0024). An ACME account is rate-limited per key and accumulates
/// issuance history, so the properties that matter are all about <em>not</em> ending up with a second
/// one: a concurrent create yields one key, a second load reuses it, and the registration survives a
/// restart on any instance.
/// </summary>
public sealed class AcmeAccountKeyTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string DirectoryUrl = "https://ca.test/directory";

    [Fact]
    public async Task AFirstLoad_GeneratesAndPersistsAPrivateKey() {
        using var host = AuthTestHost.Start();

        using var account = await Store(host).LoadOrCreateAsync(DirectoryUrl, Ct);

        Assert.Null(account.AccountUrl);
        var row = await RowAsync(host);
        Assert.NotNull(row);
        Assert.NotEmpty(row.PrivateKey);
        Assert.Equal(DirectoryUrl, row.DirectoryUrl);
    }

    [Fact]
    public async Task ASecondLoad_ReusesTheSameKey() {
        using var host = AuthTestHost.Start();
        string first;
        using (var account = await Store(host).LoadOrCreateAsync(DirectoryUrl, Ct))
            first = AcmeJws.Thumbprint(account.Key);

        using var reloaded = await Store(host).LoadOrCreateAsync(DirectoryUrl, Ct);

        Assert.Equal(first, AcmeJws.Thumbprint(reloaded.Key));
    }

    /// <summary>
    /// The reason the insert is unconditional rather than read-then-write: two instances starting at the
    /// same moment must not each register an account, or the CA ends up holding two for one deployment,
    /// each with its own rate-limit budget and half the issuance history.
    /// </summary>
    [Fact]
    public async Task ConcurrentCreates_ProduceOneKey() {
        using var host = AuthTestHost.Start();
        using var other = host.Restart();

        var loads = await Task.WhenAll(
            Store(host).LoadOrCreateAsync(DirectoryUrl, Ct),
            Store(other).LoadOrCreateAsync(DirectoryUrl, Ct),
            Store(host).LoadOrCreateAsync(DirectoryUrl, Ct));

        var thumbprints = loads.Select(a => AcmeJws.Thumbprint(a.Key)).Distinct().ToArray();
        foreach (var account in loads) account.Dispose();
        Assert.Single(thumbprints);
        Assert.Equal(1, await CountAsync(host));
    }

    /// <summary>
    /// Adopting the secret on a running installation encrypts the account key where it is read, without
    /// a migration step — and without minting a second account, which is the one thing that must never
    /// happen to an ACME account.
    /// </summary>
    [Fact]
    public async Task AdoptingTheSecretLater_EncryptsTheExistingAccountKey() {
        using var plain = AuthTestHost.Start();
        string thumbprint;
        using (var account = await Store(plain).LoadOrCreateAsync(DirectoryUrl, Ct))
            thumbprint = AcmeJws.Thumbprint(account.Key);
        Assert.Equal(KeyProtector.None, (await RowAsync(plain))!.Protection);

        using var encrypting = plain.Restart(
            ("Watchtower:Auth:KeyProtectionSecret", "a-long-enough-passphrase-for-a-test"));
        using var reloaded = await Store(encrypting).LoadOrCreateAsync(DirectoryUrl, Ct);

        Assert.Equal(thumbprint, AcmeJws.Thumbprint(reloaded.Key));
        Assert.Equal(KeyProtector.AesGcmV1, (await RowAsync(encrypting))!.Protection);
        Assert.Equal(1, await CountAsync(encrypting));
    }

    /// <summary>
    /// Fatal, and deliberately: silently regenerating would abandon the account the CA associates with
    /// this deployment — including whatever rate-limit allowance it has earned — with nothing in the log
    /// to connect the two.
    /// </summary>
    [Fact]
    public async Task ACorruptKey_Throws() {
        using var host = AuthTestHost.Start();
        using (var _ = await Store(host).LoadOrCreateAsync(DirectoryUrl, Ct)) { }
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.AcmeAccounts.Where(a => a.DirectoryUrl == DirectoryUrl)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.PrivateKey, "not a key\n"u8.ToArray()), Ct);
        }

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
            () => Store(host).LoadOrCreateAsync(DirectoryUrl, Ct));
    }

    [Fact]
    public async Task TheAccountUrl_RoundTripsAcrossLoads() {
        using var host = AuthTestHost.Start();
        using (var account = await Store(host).LoadOrCreateAsync(DirectoryUrl, Ct)) {
            await account.SetAccountUrlAsync("https://ca.test/acct/42", Ct);
            Assert.Equal("https://ca.test/acct/42", account.AccountUrl);
        }

        using var reloaded = await Store(host).LoadOrCreateAsync(DirectoryUrl, Ct);
        Assert.Equal("https://ca.test/acct/42", reloaded.AccountUrl);
        Assert.Equal("https://ca.test/acct/42", (await RowAsync(host))!.AccountUrl);
    }

    [Fact]
    public async Task ClearingTheAccountUrl_SurvivesAReload() {
        using var host = AuthTestHost.Start();
        using (var account = await Store(host).LoadOrCreateAsync(DirectoryUrl, Ct)) {
            await account.SetAccountUrlAsync("https://ca.test/acct/42", Ct);
            await account.ClearAccountUrlAsync(Ct);
        }

        using var reloaded = await Store(host).LoadOrCreateAsync(DirectoryUrl, Ct);
        Assert.Null(reloaded.AccountUrl);
    }

    /// <summary>
    /// An account exists only at the CA that issued it. Pointing Watchtower at a different directory has
    /// to produce a fresh key and a fresh registration, never one CA's account URL presented to another —
    /// which is what keying the row by directory URL buys.
    /// </summary>
    [Fact]
    public async Task AnotherDirectory_GetsItsOwnAccount() {
        using var host = AuthTestHost.Start();
        using var here = await Store(host).LoadOrCreateAsync(DirectoryUrl, Ct);
        await here.SetAccountUrlAsync("https://ca.test/acct/42", Ct);

        using var elsewhere = await Store(host).LoadOrCreateAsync("https://other-ca.test/directory", Ct);

        Assert.Null(elsewhere.AccountUrl);
        Assert.NotEqual(AcmeJws.Thumbprint(here.Key), AcmeJws.Thumbprint(elsewhere.Key));
        Assert.Equal(2, await CountAsync(host));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private static AcmeAccountStore Store(AuthTestHost host) =>
        host.Services.GetRequiredService<AcmeAccountStore>();

    private static async Task<Entities.AcmeAccount?> RowAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AcmeAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.DirectoryUrl == DirectoryUrl, Ct);
    }

    private static async Task<int> CountAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>().AcmeAccounts.CountAsync(Ct);
    }
}
