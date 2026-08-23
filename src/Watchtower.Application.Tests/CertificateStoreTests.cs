using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The in-process proxy's certificate store: what it writes, what it serves, and what it refuses. Rows
/// since ADR-0024, so the load path — the one that runs unattended on every start, and now also on every
/// change another instance makes — carries most of the weight.
/// </summary>
public sealed class CertificateStoreTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Host = "app.test";

    [Fact]
    public async Task Install_StoresTheWholeChainAndTheKey() {
        using var host = AuthTestHost.Start();
        using var chain = TestCertificates.Create(Host);
        var store = Store(host);

        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        var row = await RowAsync(host, Host);
        Assert.NotNull(row);
        // The whole chain, not just the leaf — the point of the store.
        Assert.Equal(2, row.CertificatePem.Split("-----BEGIN CERTIFICATE-----").Length - 1);
        Assert.Equal(chain.Leaf.Thumbprint, row.Thumbprint);
        Assert.Equal(ProxyCertificateSources.Acme, row.Source);
        Assert.Equal("Watchtower Test Intermediate", row.Issuer);
        Assert.NotEmpty(row.PrivateKey);
    }

    /// <summary>
    /// The ordering guarantee the whole design rests on: Kestrel is already listening before any hosted
    /// service runs, so a store that loaded lazily or in the background would answer "no certificate" to
    /// whatever arrived first. The load is asynchronous now, so it is an explicit startup step rather
    /// than a constructor — this is the test that it actually fills the map.
    /// </summary>
    [Fact]
    public async Task ANewStore_LoadsWhatIsInTheTable_WhenItIsInitialized() {
        using var host = AuthTestHost.Start();
        using var chain = TestCertificates.Create(Host);
        await Store(host).InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        // The next process start over the same database.
        using var restarted = host.Restart();
        var store = Store(restarted);

        var context = store.SelectContext(Host);
        Assert.NotNull(context);
        // Leaf plus the issuer: this is what SslStream sends, and what an empty container could not have
        // assembled for itself.
        Assert.Single(context.IntermediateCertificates);
        Assert.Equal(chain.Leaf.Thumbprint, context.TargetCertificate.Thumbprint);

        var entry = Assert.Single(store.Entries);
        Assert.Equal(Host, entry.Host);
        Assert.Equal(2, entry.ChainLength);
        Assert.Equal("Watchtower Test Intermediate", entry.IssuerCommonName);
        Assert.Equal(chain.Leaf.Thumbprint, entry.Thumbprint);
        Assert.Equal(entry, store.Find(Host));
    }

    /// <summary>
    /// The cross-instance property the table exists for: a certificate obtained on one node is served by
    /// another, without that node having ordered anything or restarted.
    /// </summary>
    [Fact]
    public async Task ASecondStoreOverTheSameDatabase_PicksUpAnInstall_OnReload() {
        using var first = AuthTestHost.Start();
        using var second = first.Restart();
        Assert.Null(Store(second).SelectContext(Host));

        using var chain = TestCertificates.Create(Host);
        await Store(first).InstallAsync(Host, chain.PemChain, chain.Key!, Ct);
        await Store(second).ReloadAsync(Ct);

        Assert.NotNull(Store(second).SelectContext(Host));
        Assert.Equal(chain.Leaf.Thumbprint, Store(second).SelectCertificate(Host)!.Thumbprint);
    }

    /// <summary>
    /// A reload fires for route changes too, so the common case is "nothing about the certificates
    /// moved". Rebuilding every context then would cost a PKCS#12 round trip per host for no reason —
    /// and, worse, would swap the object a handshake in flight is holding.
    /// </summary>
    [Fact]
    public async Task Reload_LeavesAnUnchangedCertificateExactlyWhereItWas() {
        using var host = AuthTestHost.Start();
        using var chain = TestCertificates.Create(Host);
        var store = Store(host);
        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);
        var before = store.SelectContext(Host);

        await store.ReloadAsync(Ct);

        Assert.Same(before, store.SelectContext(Host));
    }

    [Fact]
    public async Task Reload_DropsAHostAnotherInstanceDeleted() {
        using var first = AuthTestHost.Start();
        using var second = first.Restart();
        using var chain = TestCertificates.Create(Host);
        await Store(first).InstallAsync(Host, chain.PemChain, chain.Key!, Ct);
        await Store(second).ReloadAsync(Ct);
        Assert.NotNull(Store(second).SelectContext(Host));

        await Store(first).ForgetAsync(Host, Ct);
        await Store(second).ReloadAsync(Ct);

        // Continuing to answer for a host the cluster has forgotten would make the two nodes disagree
        // about which domains this deployment serves.
        Assert.Null(Store(second).SelectContext(Host));
    }

    [Fact]
    public async Task Lookups_AreCaseInsensitive_AndAnswerNothingForAnythingUnknown() {
        using var host = AuthTestHost.Start();
        using var chain = TestCertificates.Create(Host);
        var store = Store(host);
        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        // Browsers lowercase the SNI, but nothing forces them to.
        Assert.NotNull(store.SelectContext("APP.TEST"));
        Assert.NotNull(store.SelectCertificate("  App.Test  "));
        // The fully-qualified form of the same name.
        Assert.NotNull(store.SelectContext("app.test."));
        Assert.Null(store.SelectContext("."));

        Assert.Null(store.SelectContext(null));
        Assert.Null(store.SelectContext(""));
        Assert.Null(store.SelectContext("   "));
        // No fallback to "some other certificate we happen to hold".
        Assert.Null(store.SelectContext("other.test"));
        Assert.Null(store.SelectCertificate("other.test"));
        Assert.Null(store.Find("other.test"));
    }

    [Fact]
    public async Task Forget_DropsTheEntryAndItsRow() {
        using var host = AuthTestHost.Start();
        using var chain = TestCertificates.Create(Host);
        var store = Store(host);
        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        Assert.True(await store.ForgetAsync(Host, Ct));

        Assert.Null(store.SelectContext(Host));
        Assert.Empty(store.Entries);
        Assert.Null(await RowAsync(host, Host));
        // Idempotent: nothing left to remove is not a failure, it is the answer.
        Assert.False(await store.ForgetAsync(Host, Ct));
    }

    /// <summary>
    /// The startup load runs unattended over a table an operator (or a lost key-protection secret) can
    /// have made partly unreadable. One bad row has to cost that one host, not the listener.
    /// </summary>
    [Fact]
    public async Task ARowThatCannotBeLoaded_IsSkipped_AndTheRestStillLoad() {
        using var host = AuthTestHost.Start();
        using var good = TestCertificates.Create(Host);
        await Store(host).InstallAsync(Host, good.PemChain, good.Key!, Ct);
        await InsertRawAsync(host, "garbage.test", "not a certificate\n", [1, 2, 3]);

        using var restarted = host.Restart();

        Assert.NotNull(Store(restarted).SelectContext(Host));
        Assert.Single(Store(restarted).Entries);
        Assert.Null(Store(restarted).SelectContext("garbage.test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("ä.example")]
    [InlineData("app .test")]
    [InlineData("*.example")]
    [InlineData("-app.test")]
    [InlineData("app-.test")]
    [InlineData("app..test")]
    public void NormalizeHost_RefusesAnythingThatIsNotAPlainDnsName(string host) =>
        // Validated, never sanitised: every caller normalises first, so anything else here is a bug or an
        // attempt at injection, and quietly rewriting it into *some* row is the bad outcome.
        Assert.Throws<ArgumentException>(() => CertificateStore.NormalizeHost(host));

    [Fact]
    public void NormalizeHost_LowercasesWhatItAccepts() {
        Assert.Equal("app.test", CertificateStore.NormalizeHost("App.TEST"));
        Assert.Equal("a-b.c1.example", CertificateStore.NormalizeHost("a-b.c1.example"));
        Assert.Throws<ArgumentException>(
            () => CertificateStore.NormalizeHost(new string('a', 250) + ".example"));
    }

    [Fact]
    public async Task PruneUndesired_RemovesOnlyWhatIsBothUnwantedAndLongExpired() {
        using var host = AuthTestHost.Start();
        var now = DateTimeOffset.UtcNow;
        var store = Store(host);
        using var undesiredExpired = TestCertificates.Create("gone.test", now.AddDays(-90), now.AddDays(-40));
        using var undesiredFresh = TestCertificates.Create("fresh.test");
        using var desiredExpired = TestCertificates.Create("kept.test", now.AddDays(-90), now.AddDays(-40));
        await store.InstallAsync("gone.test", undesiredExpired.PemChain, undesiredExpired.Key!, Ct);
        await store.InstallAsync("fresh.test", undesiredFresh.PemChain, undesiredFresh.Key!, Ct);
        await store.InstallAsync("kept.test", desiredExpired.PemChain, desiredExpired.Key!, Ct);
        Assert.Equal(3, store.Entries.Count);

        var removed = await store.PruneUndesiredAsync(
            new HashSet<string> { "kept.test" }, TimeSpan.FromDays(30), Ct);

        Assert.Equal(1, removed);
        // Expired but still routed: keeping it is what lets a renewal replace it in place.
        Assert.NotNull(store.SelectContext("kept.test"));
        // Unwanted but perfectly valid: nothing to gain by throwing an issuance away.
        Assert.NotNull(store.SelectContext("fresh.test"));
        Assert.Null(store.SelectContext("gone.test"));
        Assert.Null(await RowAsync(host, "gone.test"));
    }

    /// <summary>ACME issues EC keys, but an operator can hand-place an RSA pair and an internal CA may only issue RSA.</summary>
    [Fact]
    public async Task AnRsaKeyPair_LoadsToo() {
        using var host = AuthTestHost.Start();
        using var chain = TestCertificates.Create(Host, rsa: true);
        // Through the import path, which is the one that takes a key as PEM — an RSA pair has no ECDsa
        // handle for InstallAsync to take.
        Assert.True(await Store(host).ImportAsync(Host, chain.PemChain, chain.KeyPem, Ct));

        using var restarted = host.Restart();

        Assert.NotNull(Store(restarted).SelectContext(Host));
        Assert.Equal(chain.Leaf.Thumbprint, Store(restarted).SelectCertificate(Host)!.Thumbprint);
    }

    [Fact]
    public async Task ACertificateThatIsNotValidYet_IsNotServed() {
        using var host = AuthTestHost.Start();
        var now = DateTimeOffset.UtcNow;
        using var future = TestCertificates.Create("future.test", now.AddHours(1), now.AddDays(90));
        using var good = TestCertificates.Create(Host);
        await Store(host).InstallAsync("future.test", future.PemChain, future.Key!, Ct);
        await Store(host).InstallAsync(Host, good.PemChain, good.Key!, Ct);

        using var restarted = host.Restart();

        // Serving it would produce a browser error rather than a line in our log.
        Assert.Null(Store(restarted).SelectContext("future.test"));
        Assert.NotNull(Store(restarted).SelectContext(Host));
        // The row is kept: it becomes servable on its own, at the hour it says.
        Assert.NotNull(await RowAsync(restarted, "future.test"));
    }

    /// <summary>
    /// Expired is different: refusing the handshake looks to a visitor like the site is gone, while
    /// serving the stale certificate at least says what is wrong.
    /// </summary>
    [Fact]
    public async Task AnExpiredCertificate_IsStillServed() {
        using var host = AuthTestHost.Start();
        var now = DateTimeOffset.UtcNow;
        using var expired = TestCertificates.Create(Host, now.AddDays(-90), now.AddDays(-1));
        await Store(host).ImportAsync(Host, expired.PemChain, expired.KeyPem, Ct);

        using var restarted = host.Restart();

        Assert.NotNull(Store(restarted).SelectContext(Host));
    }

    [Fact]
    public async Task AnEmptyTable_IsAnEmptyStore_NotAFailedStart() {
        using var host = AuthTestHost.Start();

        Assert.Empty(Store(host).Entries);
        Assert.Null(Store(host).SelectContext(Host));
    }

    [Fact]
    public async Task Install_ReplacesWhatWasThere() {
        using var host = AuthTestHost.Start();
        using var first = TestCertificates.Create(Host);
        using var second = TestCertificates.Create(Host);
        var store = Store(host);

        await store.InstallAsync(Host, first.PemChain, first.Key!, Ct);
        await store.InstallAsync(Host, second.PemChain, second.Key!, Ct);

        Assert.Equal(second.Leaf.Thumbprint, store.SelectCertificate(Host)!.Thumbprint);
        Assert.Single(store.Entries);
        // One row per host, not one per issuance: the unique index is what a renewal upserts against.
        Assert.Equal(1, await CountAsync(host));
    }

    /// <summary>
    /// A renewal must not pull the rug out from under a handshake that is already running. The context
    /// keeps the leaf instance it was created with as its target, so releasing the replaced certificate
    /// would take the key with it — mid-handshake, on the one connection that had the bad luck.
    /// </summary>
    [Fact]
    public async Task Install_LeavesTheContextItReplaced_Usable() {
        using var host = AuthTestHost.Start();
        using var first = TestCertificates.Create(Host);
        using var second = TestCertificates.Create(Host);
        var store = Store(host);
        await store.InstallAsync(Host, first.PemChain, first.Key!, Ct);

        // What a handshake in flight would be holding.
        var inFlight = store.SelectContext(Host)!;
        await store.InstallAsync(Host, second.PemChain, second.Key!, Ct);

        Assert.NotEmpty(inFlight.TargetCertificate.GetRawCertData());
        using var key = inFlight.TargetCertificate.GetECDsaPrivateKey();
        Assert.NotNull(key);
    }

    [Fact]
    public async Task Forget_LeavesTheContextItRemoved_Usable() {
        using var host = AuthTestHost.Start();
        using var chain = TestCertificates.Create(Host);
        var store = Store(host);
        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        var inFlight = store.SelectContext(Host)!;
        Assert.True(await store.ForgetAsync(Host, Ct));

        Assert.NotEmpty(inFlight.TargetCertificate.GetRawCertData());
    }

    [Fact]
    public async Task Install_RefusesMaterialThatCouldNeverBeServed() {
        using var host = AuthTestHost.Start();
        using var chain = TestCertificates.Create(Host);
        var store = Store(host);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.InstallAsync("../escape", chain.PemChain, chain.Key!, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.InstallAsync(Host, "   ", chain.Key!, Ct));

        // Nothing reached the table.
        Assert.Equal(0, await CountAsync(host));
    }

    /// <summary>
    /// The import is the upgrade path's, and must never overwrite a certificate that has been issued
    /// since: a row in the table is newer than anything left on the old volume, by construction.
    /// </summary>
    [Fact]
    public async Task Import_LeavesAnExistingRowAlone() {
        using var host = AuthTestHost.Start();
        using var issued = TestCertificates.Create(Host);
        using var onDisk = TestCertificates.Create(Host);
        var store = Store(host);
        await store.InstallAsync(Host, issued.PemChain, issued.Key!, Ct);

        Assert.False(await store.ImportAsync(Host, onDisk.PemChain, onDisk.KeyPem, Ct));

        var row = await RowAsync(host, Host);
        Assert.Equal(issued.Leaf.Thumbprint, row!.Thumbprint);
        Assert.Equal(ProxyCertificateSources.Acme, row.Source);
    }

    /// <summary>
    /// Two instances finishing an order for one host at the same moment. The issuer lease makes this
    /// unlikely rather than impossible — a handover mid-order is exactly the window — so both the
    /// insert race (the unique index) and the update race (the xmin token) have to end in one row and
    /// no exception, with both stores serving the certificate that actually landed.
    /// </summary>
    [Fact]
    public async Task TwoStoresInstallingTheSameHostAtOnce_LeaveOneRowAndNoException() {
        using var a = AuthTestHost.Start();
        using var b = a.Restart();
        using var first = TestCertificates.Create(Host);
        using var second = TestCertificates.Create(Host);

        // The insert race: neither store has a row yet.
        await Task.WhenAll(
            Store(a).InstallAsync(Host, first.PemChain, first.Key!, Ct),
            Store(b).InstallAsync(Host, second.PemChain, second.Key!, Ct));

        Assert.Equal(1, await CountAsync(a));
        var winner = (await RowAsync(a, Host))!.Thumbprint;
        // Whichever landed, both are serving it — each store re-reads the row it wrote through.
        Assert.Equal(winner, Store(a).SelectCertificate(Host)!.Thumbprint);
        Assert.Equal(winner, Store(b).SelectCertificate(Host)!.Thumbprint);

        // The update race: now the row exists, and both stores read it before either writes.
        using var third = TestCertificates.Create(Host);
        using var fourth = TestCertificates.Create(Host);
        await Task.WhenAll(
            Store(a).InstallAsync(Host, third.PemChain, third.Key!, Ct),
            Store(b).InstallAsync(Host, fourth.PemChain, fourth.Key!, Ct));

        Assert.Equal(1, await CountAsync(a));
        var renewed = (await RowAsync(a, Host))!.Thumbprint;
        Assert.Equal(renewed, Store(a).SelectCertificate(Host)!.Thumbprint);
        Assert.Equal(renewed, Store(b).SelectCertificate(Host)!.Thumbprint);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private static CertificateStore Store(AuthTestHost host) =>
        host.Services.GetRequiredService<CertificateStore>();

    private static async Task<ProxyCertificate?> RowAsync(AuthTestHost host, string domain) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.ProxyCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Host == domain, Ct);
    }

    private static async Task<int> CountAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>()
            .ProxyCertificates.CountAsync(Ct);
    }

    /// <summary>Writes a row the store cannot possibly materialize — the corrupt-volume case, as a row.</summary>
    private static async Task InsertRawAsync(AuthTestHost host, string domain, string pem, byte[] key) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.ProxyCertificates.Add(new ProxyCertificate {
            Host = domain,
            CertificatePem = pem,
            PrivateKey = key,
            Protection = "none",
            NotBefore = DateTimeOffset.UtcNow.AddDays(-1),
            NotAfter = DateTimeOffset.UtcNow.AddDays(60),
            Issuer = "nobody",
            Thumbprint = "0",
            Source = ProxyCertificateSources.FileImport,
            InstalledAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }
}
