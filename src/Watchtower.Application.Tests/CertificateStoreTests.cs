using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The in-process proxy's certificate store: what it writes, what it serves, and what it refuses. The
/// load path carries most of the weight because it is the one that runs unattended on every start, over
/// a directory an operator can have put anything into.
/// </summary>
public sealed class CertificateStoreTests : IDisposable {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Host = "app.test";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "watchtower-cert-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Install_WritesTheChainAndTheKeyWithOwnerOnlyPermissions() {
        using var chain = TestCertificates.Create(Host);
        using var store = Open();

        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        var directory = Path.Combine(_root, Host);
        var certPath = Path.Combine(directory, "cert.pem");
        Assert.True(File.Exists(certPath));
        Assert.True(File.Exists(Path.Combine(directory, "meta.json")));
        // The whole chain, not just the leaf — the point of the store.
        Assert.Equal(2, File.ReadAllText(certPath).Split("-----BEGIN CERTIFICATE-----").Length - 1);
        // No temporary left behind by the atomic write.
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

        if (!OperatingSystem.IsWindows()) {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(directory, "key.pem")));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(certPath));
        }
    }

    /// <summary>
    /// The ordering guarantee the whole design rests on: Kestrel is already listening before any hosted
    /// service runs, so a store that loaded lazily or in the background would answer "no certificate" to
    /// whatever arrived first.
    /// </summary>
    [Fact]
    public async Task ANewStore_LoadsWhatIsOnDisk_InItsConstructor() {
        using var chain = TestCertificates.Create(Host);
        using (var writer = Open()) await writer.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        using var store = Open();

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

    [Fact]
    public async Task Lookups_AreCaseInsensitive_AndAnswerNothingForAnythingUnknown() {
        using var chain = TestCertificates.Create(Host);
        using var store = Open();
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
    public async Task Forget_DropsTheEntryAndItsFiles() {
        using var chain = TestCertificates.Create(Host);
        using var store = Open();
        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        Assert.True(store.Forget(Host, deleteFiles: true));

        Assert.Null(store.SelectContext(Host));
        Assert.Empty(store.Entries);
        Assert.False(Directory.Exists(Path.Combine(_root, Host)));
        // Idempotent: nothing left to remove is not a failure, it is the answer.
        Assert.False(store.Forget(Host, deleteFiles: true));
    }

    [Fact]
    public async Task Forget_WithoutDeletingFiles_KeepsThemForAReIssueFreeRestart() {
        using var chain = TestCertificates.Create(Host);
        using var store = Open();
        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        Assert.True(store.Forget(Host, deleteFiles: false));

        Assert.Null(store.SelectContext(Host));
        Assert.True(File.Exists(Path.Combine(_root, Host, "cert.pem")));
    }

    /// <summary>
    /// The startup scan runs unattended over a mounted volume. One unreadable directory has to cost that
    /// one host, not the whole listener.
    /// </summary>
    [Fact]
    public void ADirectoryThatCannotBeLoaded_IsSkipped_AndTheRestStillLoad() {
        using var good = TestCertificates.Create(Host);
        good.WriteTo(_root);

        // Three ways it goes wrong in practice: garbage in the file, a half-written directory with no
        // key, and something that is not a host name at all.
        Directory.CreateDirectory(Path.Combine(_root, "garbage.test"));
        File.WriteAllText(Path.Combine(_root, "garbage.test", "cert.pem"), "not a certificate\n");
        using var keyless = TestCertificates.Create("keyless.test");
        keyless.WriteTo(_root);
        File.Delete(Path.Combine(_root, "keyless.test", "key.pem"));
        Directory.CreateDirectory(Path.Combine(_root, "not a host"));

        var log = new CollectingLogger();
        using var store = Open(log);

        Assert.NotNull(store.SelectContext(Host));
        Assert.Single(store.Entries);
        Assert.Null(store.SelectContext("garbage.test"));
        Assert.Null(store.SelectContext("keyless.test"));
        Assert.Equal(3, log.Warnings.Count);
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
    public void HostDirectoryName_RefusesAnythingThatIsNotAPlainDnsName(string host) =>
        // Validated, never sanitised: every caller normalises first, so anything else here is a bug or an
        // attempt at path traversal, and quietly rewriting it into *some* directory is the bad outcome.
        Assert.Throws<ArgumentException>(() => CertificateStore.HostDirectoryName(host));

    [Fact]
    public void HostDirectoryName_LowercasesWhatItAccepts() {
        Assert.Equal("app.test", CertificateStore.HostDirectoryName("App.TEST"));
        Assert.Equal("a-b.c1.example", CertificateStore.HostDirectoryName("a-b.c1.example"));
        Assert.Throws<ArgumentException>(
            () => CertificateStore.HostDirectoryName(new string('a', 250) + ".example"));
    }

    [Fact]
    public void PruneUndesired_RemovesOnlyWhatIsBothUnwantedAndLongExpired() {
        var now = DateTimeOffset.UtcNow;
        using var undesiredExpired = TestCertificates.Create(
            "gone.test", now.AddDays(-90), now.AddDays(-40));
        using var undesiredFresh = TestCertificates.Create("fresh.test");
        using var desiredExpired = TestCertificates.Create(
            "kept.test", now.AddDays(-90), now.AddDays(-40));
        undesiredExpired.WriteTo(_root);
        undesiredFresh.WriteTo(_root);
        desiredExpired.WriteTo(_root);

        using var store = Open();
        Assert.Equal(3, store.Entries.Count);

        var removed = store.PruneUndesired(
            new HashSet<string> { "kept.test" }, TimeSpan.FromDays(30));

        Assert.Equal(1, removed);
        // Expired but still routed: keeping it is what lets a renewal replace it in place.
        Assert.NotNull(store.SelectContext("kept.test"));
        // Unwanted but perfectly valid: nothing to gain by throwing an issuance away.
        Assert.NotNull(store.SelectContext("fresh.test"));
        Assert.Null(store.SelectContext("gone.test"));
        Assert.False(Directory.Exists(Path.Combine(_root, "gone.test")));
    }

    /// <summary>ACME issues EC keys, but an operator can hand-place an RSA pair and an internal CA may only issue RSA.</summary>
    [Fact]
    public void AnRsaKeyPair_LoadsToo() {
        using var chain = TestCertificates.Create(Host, rsa: true);
        chain.WriteTo(_root);

        using var store = Open();

        Assert.NotNull(store.SelectContext(Host));
        Assert.Equal(chain.Leaf.Thumbprint, store.SelectCertificate(Host)!.Thumbprint);
    }

    [Fact]
    public void ACertificateThatIsNotValidYet_IsNotServed() {
        var now = DateTimeOffset.UtcNow;
        using var future = TestCertificates.Create("future.test", now.AddHours(1), now.AddDays(90));
        future.WriteTo(_root);
        using var good = TestCertificates.Create(Host);
        good.WriteTo(_root);

        var log = new CollectingLogger();
        using var store = Open(log);

        // Serving it would produce a browser error rather than a line in our log.
        Assert.Null(store.SelectContext("future.test"));
        Assert.NotNull(store.SelectContext(Host));
        Assert.Contains(log.Warnings, w => w.Contains("future.test", StringComparison.Ordinal));
    }

    /// <summary>
    /// Expired is different: refusing the handshake looks to a visitor like the site is gone, while
    /// serving the stale certificate at least says what is wrong.
    /// </summary>
    [Fact]
    public void AnExpiredCertificate_IsStillServed_ButSaidSoAbout() {
        var now = DateTimeOffset.UtcNow;
        using var expired = TestCertificates.Create(Host, now.AddDays(-90), now.AddDays(-1));
        expired.WriteTo(_root);

        var log = new CollectingLogger();
        using var store = Open(log);

        Assert.NotNull(store.SelectContext(Host));
        Assert.Contains(log.Warnings, w => w.Contains("expired", StringComparison.Ordinal));
    }

    /// <summary>
    /// The store is constructed while Kestrel is being configured, so anything it throws fails the whole
    /// host. A cert directory the process cannot read is a plausible mount mistake and must cost the
    /// certificates, not the process.
    /// </summary>
    [Fact]
    public void AnUnreadableCertificateDirectory_IsAnEmptyStore_NotAFailedStart() {
        if (OperatingSystem.IsWindows()) {
            Assert.Skip("Unix file permissions.");
            return;
        }

        using var chain = TestCertificates.Create(Host);
        chain.WriteTo(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try {
            // root ignores the permission bits, so on that account the condition under test does not
            // exist — asked of the filesystem rather than of the uid, which is what actually decides.
            Assert.SkipWhen(CanList(_root), "The current account can read the directory anyway (root?).");

            var log = new CollectingLogger();
            using var store = Open(log);

            Assert.Empty(store.Entries);
            Assert.Contains(log.Warnings, w => w.Contains("Could not read", StringComparison.Ordinal));
        } finally {
            // Readable again, or the fixture cleanup cannot remove it either.
            File.SetUnixFileMode(
                _root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool CanList(string path) {
        try {
            Directory.GetDirectories(path);
            return true;
        } catch (UnauthorizedAccessException) {
            return false;
        }
    }

    [Fact]
    public void AMissingCertificateDirectory_IsAnEmptyStore_NotAFailedStart() {
        using var store = Open();

        Assert.Empty(store.Entries);
        Assert.Null(store.SelectContext(Host));
    }

    [Fact]
    public async Task Install_ReplacesWhatWasThere() {
        using var first = TestCertificates.Create(Host);
        using var second = TestCertificates.Create(Host);
        using var store = Open();

        await store.InstallAsync(Host, first.PemChain, first.Key!, Ct);
        await store.InstallAsync(Host, second.PemChain, second.Key!, Ct);

        Assert.Equal(second.Leaf.Thumbprint, store.SelectCertificate(Host)!.Thumbprint);
        Assert.Single(store.Entries);
    }

    /// <summary>
    /// A renewal must not pull the rug out from under a handshake that is already running. The context
    /// keeps the leaf instance it was created with as its target, so releasing the replaced certificate
    /// would take the key with it — mid-handshake, on the one connection that had the bad luck.
    /// </summary>
    [Fact]
    public async Task Install_LeavesTheContextItReplaced_Usable() {
        using var first = TestCertificates.Create(Host);
        using var second = TestCertificates.Create(Host);
        using var store = Open();
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
        using var chain = TestCertificates.Create(Host);
        using var store = Open();
        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        var inFlight = store.SelectContext(Host)!;
        Assert.True(store.Forget(Host, deleteFiles: true));

        Assert.NotEmpty(inFlight.TargetCertificate.GetRawCertData());
    }

    [Fact]
    public async Task Install_RefusesMaterialThatCouldNeverBeServed() {
        using var chain = TestCertificates.Create(Host);
        using var store = Open();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.InstallAsync("../escape", chain.PemChain, chain.Key!, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.InstallAsync(Host, "   ", chain.Key!, Ct));

        // Nothing reached the disk.
        Assert.False(Directory.Exists(Path.Combine(_root, Host)));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private CertificateStore Open(ILogger<CertificateStore>? logger = null) => new(
        new StaticOptionsMonitor(new WatchtowerOptions {
            Proxy = new ProxyOptions { Yarp = new YarpProxyOptions { CertPath = _root } },
        }),
        TimeProvider.System,
        logger ?? NullLogger<CertificateStore>.Instance);

    private sealed class StaticOptionsMonitor(WatchtowerOptions value) : IOptionsMonitor<WatchtowerOptions> {
        public WatchtowerOptions CurrentValue { get; } = value;
        public WatchtowerOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<WatchtowerOptions, string?> listener) => null;
    }

    /// <summary>Captures the warnings, which are the only way a skipped directory announces itself.</summary>
    private sealed class CollectingLogger : ILogger<CertificateStore> {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
