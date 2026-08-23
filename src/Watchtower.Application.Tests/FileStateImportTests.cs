using System.Security.Cryptography;
using System.Text.Json;
using Elarion.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The one-shot upgrade step that carries a pre-ADR-0024 installation's <c>/data</c> files into the
/// database. What it is really for is the fourth test here: without the data-protection key ring, the
/// upgrade would invalidate every payload the ring protects, which looks to users like an outage.
/// </summary>
/// <remarks>
/// The host is started with the legacy directories laid out first, because the import runs as part of
/// <c>InitializeWatchtowerStateAsync</c> — the same step the deployment runs after migrating. Asserting
/// against a host that imported on its own start is the only way to know the ordering is right: the
/// import has to happen before the signer and the store read their tables, or the first start after an
/// upgrade generates a key next to the one it was about to import.
/// </remarks>
public sealed class FileStateImportTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Host = "app.example.invalid";
    private const string DirectoryUrl = "https://ca.test/directory";

    [Fact]
    public async Task TheSigningKey_IsImported_SoIssuedAssertionsStayVerifiable() {
        var volume = new LegacyVolume();
        var keyPem = volume.WriteSigningKey();

        using var host = volume.Start();

        var row = await RowAsync<SigningKey>(host, db => db.SigningKeys);
        Assert.NotNull(row);
        // The same key, not a fresh one: an app that cached the JWKS keys off the kid.
        using var expected = ECDsa.Create();
        expected.ImportFromPem(keyPem);
        Assert.Equal(
            Thumbprint(expected), Thumbprint(Import(host, row.PrivateKey, row.Protection, "identity-assertion")));
        Assert.Equal(row.KeyId, host.Services.GetRequiredService<AuthTokenSigner>().KeyId);
    }

    [Fact]
    public async Task TheAcmeAccount_IsImported_WithTheUrlTheCaIssued() {
        var volume = new LegacyVolume();
        volume.WriteAcmeAccount("https://ca.test/acct/7");

        using var host = volume.Start();

        var row = await RowAsync<Entities.AcmeAccount>(host, db => db.AcmeAccounts);
        Assert.NotNull(row);
        Assert.Equal(DirectoryUrl, row.DirectoryUrl);
        // The account URL is what stops the next start re-registering — and a registration is a slot in
        // the CA's rate limit.
        Assert.Equal("https://ca.test/acct/7", row.AccountUrl);
    }

    [Fact]
    public async Task Certificates_AreImported_AndServed() {
        var volume = new LegacyVolume();
        using var chain = volume.WriteCertificate(Host);

        using var host = volume.Start();

        var store = host.Services.GetRequiredService<CertificateStore>();
        Assert.Equal(chain.Leaf.Thumbprint, store.SelectCertificate(Host)!.Thumbprint);
        var row = await RowAsync<ProxyCertificate>(host, db => db.ProxyCertificates);
        Assert.Equal(ProxyCertificateSources.FileImport, row!.Source);
    }

    /// <summary>
    /// The reason the import exists at all. The data-protection ring is what password-reset tokens and
    /// the OIDC correlation/nonce cookies are keyed on; losing it on upgrade day is a user-visible
    /// failure with no obvious cause.
    /// </summary>
    [Fact]
    public async Task TheDataProtectionKeyRing_IsImported_SoProtectedPayloadsSurvive() {
        var volume = new LegacyVolume();
        // A real ring, written by ASP.NET itself into the legacy directory, and a payload protected with
        // it — which is exactly the shape of the thing that has to keep working.
        var protectedPayload = volume.WriteDataProtectionRing("a session's protected payload");

        using var host = volume.Start();

        Assert.NotEmpty(await ListAsync<DataProtectionKey>(host, db => db.DataProtectionKeys));
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("watchtower-test");
        Assert.Equal("a session's protected payload", protector.Unprotect(protectedPayload));
    }

    /// <summary>
    /// Idempotent, and not only through the sentinel: an import that ran twice must not overwrite a
    /// certificate issued since, or resurrect a key ring somebody deliberately rotated.
    /// </summary>
    [Fact]
    public async Task RunningItAgain_ChangesNothing() {
        var volume = new LegacyVolume();
        using var chain = volume.WriteCertificate(Host);
        volume.WriteSigningKey();
        using var host = volume.Start();
        var before = await RowAsync<SigningKey>(host, db => db.SigningKeys);

        // The sentinel cleared, so the second pass genuinely walks the directories again.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
            await settings.RemoveAsync(
                WatchtowerSettingPaths.AuthFileStateImported, SettingsScope.Global, null, Ct);
            await scope.ServiceProvider.GetRequiredService<FileStateImport>().RunAsync(Ct);
        }

        Assert.Equal(1, await CountAsync(host, db => db.SigningKeys));
        Assert.Equal(1, await CountAsync(host, db => db.ProxyCertificates));
        Assert.Equal(before!.KeyId, (await RowAsync<SigningKey>(host, db => db.SigningKeys))!.KeyId);
    }

    [Fact]
    public async Task TheSentinelIsWritten_EvenWhenThereIsNothingToImport() {
        using var host = AuthTestHost.Start();

        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        Assert.NotNull(await settings.GetStringAsync(
            WatchtowerSettingPaths.AuthFileStateImported, SettingsScope.Global, Ct));
        // A fresh install has no files and should not walk two directories on every restart forever.
        Assert.Equal(0, await CountAsync(host, db => db.ProxyCertificates));
    }

    /// <summary>The files are the operator's rollback path; the import must not be a move.</summary>
    [Fact]
    public async Task TheFilesAreLeftWhereTheyAre() {
        var volume = new LegacyVolume();
        using var chain = volume.WriteCertificate(Host);
        volume.WriteSigningKey();

        using var host = volume.Start();

        Assert.True(File.Exists(Path.Combine(volume.KeyPath, AuthTokenSigner.KeyFileName)));
        Assert.True(File.Exists(Path.Combine(volume.CertPath, Host, "cert.pem")));
        Assert.True(File.Exists(Path.Combine(volume.CertPath, Host, "key.pem")));
    }

    /// <summary>One unreadable file must cost that one artefact, not the host's start.</summary>
    [Fact]
    public async Task AnUnreadableFile_IsSkipped_AndTheRestStillImport() {
        var volume = new LegacyVolume();
        using var chain = volume.WriteCertificate(Host);
        Directory.CreateDirectory(volume.KeyPath);
        await File.WriteAllTextAsync(
            Path.Combine(volume.KeyPath, AuthTokenSigner.KeyFileName), "not a key\n", Ct);

        using var host = volume.Start();

        Assert.Equal(1, await CountAsync(host, db => db.ProxyCertificates));
        // A key was still needed, so one was generated — the deployment works, the old assertions do not.
        Assert.NotNull(host.Services.GetRequiredService<AuthTokenSigner>().KeyId);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>A <c>/data</c> volume laid out the way the pre-ADR-0024 image left it.</summary>
    private sealed class LegacyVolume {
        /// <summary>
        /// Pinned on both sides of the upgrade. Data protection derives part of its key from an
        /// application discriminator, which in a deployment is the container's content root and does not
        /// change across an image swap; naming it here is how the test models "the same application"
        /// rather than depending on what two in-process containers happen to default to.
        /// </summary>
        private const string ApplicationName = "watchtower-file-state-import-test";

        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "watchtower-legacy-volume", Guid.NewGuid().ToString("N"));

        public string KeyPath => Path.Combine(_root, "auth-keys");
        public string CertPath => Path.Combine(_root, "proxy-certs");

        /// <summary>Starts a host pointed at this volume, which imports it as part of starting.</summary>
        public AuthTestHost Start() => AuthTestHost.Start(
            services => services.AddDataProtection().SetApplicationName(ApplicationName),
            ("Watchtower:Auth:KeyPath", KeyPath),
            ("Watchtower:Proxy:Yarp:CertPath", CertPath));

        public string WriteSigningKey() {
            Directory.CreateDirectory(KeyPath);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pem = key.ExportPkcs8PrivateKeyPem();
            File.WriteAllText(Path.Combine(KeyPath, AuthTokenSigner.KeyFileName), pem);
            return pem;
        }

        /// <summary>
        /// Writes a real ASP.NET key ring into the legacy directory and returns a payload protected with
        /// it. A hand-written XML file would prove the rows arrive; only a real ring proves they work.
        /// </summary>
        public string WriteDataProtectionRing(string payload) {
            Directory.CreateDirectory(KeyPath);
            var services = new ServiceCollection();
            services.AddDataProtection()
                .SetApplicationName(ApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(KeyPath));
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("watchtower-test")
                .Protect(payload);
        }

        public void WriteAcmeAccount(string accountUrl) {
            var directory = Path.Combine(
                CertPath, "accounts", Convert.ToHexStringLower(
                    SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DirectoryUrl)).AsSpan(0, 8)));
            Directory.CreateDirectory(directory);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(Path.Combine(directory, "account.key"), key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(
                Path.Combine(directory, "account.json"),
                JsonSerializer.Serialize(
                    new AcmeAccountFile { DirectoryUrl = DirectoryUrl, AccountUrl = accountUrl },
                    AcmeJsonContext.Default.AcmeAccountFile));
        }

        public TestChain WriteCertificate(string host) {
            var chain = TestCertificates.Create(host);
            chain.WriteTo(CertPath);
            return chain;
        }
    }

    private static ECDsa Import(AuthTestHost host, byte[] stored, string protection, string purpose) {
        var key = ECDsa.Create();
        key.ImportFromPem(
            host.Services.GetRequiredService<KeyProtector>().UnprotectText(stored, protection, purpose));
        return key;
    }

    private static string Thumbprint(ECDsa key) =>
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static async Task<T?> RowAsync<T>(
        AuthTestHost host, Func<WatchtowerDbContext, DbSet<T>> set) where T : class {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await set(db).AsNoTracking().FirstOrDefaultAsync(Ct);
    }

    private static async Task<List<T>> ListAsync<T>(
        AuthTestHost host, Func<WatchtowerDbContext, DbSet<T>> set) where T : class {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await set(db).AsNoTracking().ToListAsync(Ct);
    }

    private static async Task<int> CountAsync<T>(
        AuthTestHost host, Func<WatchtowerDbContext, DbSet<T>> set) where T : class {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await set(db).CountAsync(Ct);
    }
}
